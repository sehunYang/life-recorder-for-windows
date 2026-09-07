using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LifeRecorderWin.Upload;

/// <summary>
/// Google Drive REST v3 최소 클라이언트: 폴더 찾기/만들기 + 재개 가능 업로드.
/// 안드로이드 <c>DriveClient.kt</c> 를 거의 그대로 옮긴 것이라 동작이 같다.
/// </summary>
internal sealed class DriveClient
{
    public sealed class AuthException : IOException
    {
        public AuthException() : base("Drive 인증 만료") { }
    }

    public sealed class SessionGoneException : IOException
    {
        public SessionGoneException() : base("업로드 세션 만료") { }
    }

    public sealed class NotFoundException : IOException
    {
        public NotFoundException(string message) : base(message) { }
    }

    public readonly record struct Uploaded(string Id, long? Size, string? Md5);

    public abstract record Progress
    {
        public sealed record Continue(long NextOffset) : Progress;
        public sealed record Done(Uploaded Uploaded) : Progress;
    }

    private const string Api = "https://www.googleapis.com/drive/v3";
    private const string UploadApi = "https://www.googleapis.com/upload/drive/v3";
    private const string FolderMime = "application/vnd.google-apps.folder";

    private readonly HttpClient _http;
    private readonly string _token;

    public DriveClient(HttpClient http, string token)
    {
        _http = http;
        _token = token;
    }

    private HttpRequestMessage Authed(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return req;
    }

    // ── 폴더 ─────────────────────────────────────────────────────────────────

    public async Task<string> EnsureFolderAsync(string name, string? parentId, CancellationToken ct) =>
        await FindFolderAsync(name, parentId, ct) ?? await CreateFolderAsync(name, parentId, ct);

    private async Task<string?> FindFolderAsync(string name, string? parentId, CancellationToken ct)
    {
        var parent = parentId ?? "root";
        var q = $"name = '{name.Replace("'", "\\'")}' and mimeType = '{FolderMime}' "
                + $"and '{parent}' in parents and trashed = false";
        var url = $"{Api}/files?q={Uri.EscapeDataString(q)}&fields={Uri.EscapeDataString("files(id,name)")}&pageSize=5";

        using var res = await _http.SendAsync(Authed(HttpMethod.Get, url), ct);
        var body = await CheckAsync(res, ct);
        using var doc = JsonDocument.Parse(body);
        var files = doc.RootElement.GetProperty("files");
        return files.GetArrayLength() > 0 ? files[0].GetProperty("id").GetString() : null;
    }

    private async Task<string> CreateFolderAsync(string name, string? parentId, CancellationToken ct)
    {
        var meta = new Dictionary<string, object>
        {
            ["name"] = name,
            ["mimeType"] = FolderMime,
            ["parents"] = new[] { parentId ?? "root" },
        };
        var req = Authed(HttpMethod.Post, $"{Api}/files?fields=id");
        req.Content = JsonBody(meta);
        using var res = await _http.SendAsync(req, ct);
        var body = await CheckAsync(res, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    // ── 재개 가능 업로드 ─────────────────────────────────────────────────────

    /// <summary>세션을 열고 세션 URI 를 돌려준다.</summary>
    public async Task<string> StartSessionAsync(FileInfo file, string parentId, string mime, CancellationToken ct)
    {
        var meta = new Dictionary<string, object>
        {
            ["name"] = file.Name,
            ["parents"] = new[] { parentId },
        };
        var req = Authed(HttpMethod.Post, $"{UploadApi}/files?uploadType=resumable&fields=id,size,md5Checksum");
        req.Content = JsonBody(meta);
        req.Headers.TryAddWithoutValidation("X-Upload-Content-Type", mime);
        req.Headers.TryAddWithoutValidation("X-Upload-Content-Length", file.Length.ToString());

        using var res = await _http.SendAsync(req, ct);
        await CheckAsync(res, ct);
        if (!res.Headers.TryGetValues("Location", out var loc))
            throw new IOException("업로드 세션 응답에 Location 이 없음");
        return loc.First();
    }

    /// <summary>세션이 어디까지 받았는지 묻는다.</summary>
    public async Task<Progress> QueryStatusAsync(string session, long total, CancellationToken ct)
    {
        var req = Authed(HttpMethod.Put, session);
        req.Content = new ByteArrayContent(Array.Empty<byte>());
        req.Content.Headers.ContentLength = 0;
        req.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes */{total}");
        using var res = await _http.SendAsync(req, ct);
        return await ParseUploadAsync(res, ct);
    }

    public async Task<Progress> UploadChunkAsync(string session, FileInfo file, long offset, long total, CancellationToken ct)
    {
        var len = (int)Math.Min(Config.UploadChunkBytes, total - offset);
        var bytes = new byte[len];
        await using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fs.Position = offset;
            await fs.ReadExactlyAsync(bytes, 0, len, ct);
        }

        var req = Authed(HttpMethod.Put, session);
        req.Content = new ByteArrayContent(bytes);
        req.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {offset}-{offset + len - 1}/{total}");
        using var res = await _http.SendAsync(req, ct);
        return await ParseUploadAsync(res, ct);
    }

    private static async Task<Progress> ParseUploadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var code = (int)res.StatusCode;
        switch (code)
        {
            // 308 Resume Incomplete. 어디까지 받았는지는 Range 헤더에 있다.
            case 308:
            {
                var next = 0L;
                if (res.Headers.TryGetValues("Range", out var range))
                {
                    var s = range.First();
                    var dash = s.LastIndexOf('-');
                    if (dash >= 0 && long.TryParse(s[(dash + 1)..].Trim(), out var end)) next = end + 1;
                }
                return new Progress.Continue(next);
            }
            case 200:
            case 201:
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                var root = doc.RootElement;
                return new Progress.Done(new Uploaded(
                    root.GetProperty("id").GetString()!,
                    root.TryGetProperty("size", out var sz) && long.TryParse(sz.GetString(), out var n) ? n : null,
                    root.TryGetProperty("md5Checksum", out var m) ? m.GetString() : null));
            }
            case 401:
                throw new AuthException();
            case 404:
            case 410:
                throw new SessionGoneException();
            default:
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                throw new IOException($"HTTP {code}: {DriveAuth.Truncate(body)}");
            }
        }
    }

    private static StringContent JsonBody(object o) =>
        new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    private static async Task<string> CheckAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var body = await res.Content.ReadAsStringAsync(ct);
        switch (res.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new AuthException();
            case HttpStatusCode.NotFound:
                throw new NotFoundException("HTTP 404: " + DriveAuth.Truncate(body));
        }
        if (!res.IsSuccessStatusCode)
            throw new IOException($"HTTP {(int)res.StatusCode}: {DriveAuth.Truncate(body)}");
        return body;
    }
}
