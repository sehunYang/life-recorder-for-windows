using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LifeRecorderWin.Upload;

/// <summary>
/// Google Drive 액세스 토큰. 안드로이드 <c>DriveAuth.kt</c> 의 자리다.
///
/// 안드로이드는 Play 서비스가 토큰을 대신 들고 갱신해 줘서 앱이 보관할 게 없었다.
/// 데스크톱에는 그런 게 없으므로 설치형 앱의 표준 흐름을 직접 돈다:
/// 루프백(127.0.0.1) 리디렉션 + PKCE 로 한 번 동의를 받고, 리프레시 토큰을 DPAPI 로 보관한다.
///
/// 스코프가 안드로이드(<c>drive.file</c>)와 다른 이유는 <see cref="Config.OAuthScope"/> 에 적어 뒀다.
/// </summary>
internal sealed class DriveAuth
{
    public sealed class NotLinkedException : Exception
    {
        public NotLinkedException(string message) : base(message) { }
    }

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly object _lock = new();
    private string? _accessToken;
    private DateTime _expiresAt = DateTime.MinValue;

    public bool IsLinked => Credentials.Load().HasToken;
    public bool HasClient => Credentials.Load().HasClient;

    /// <summary>
    /// UI 없이 액세스 토큰을 얻는다. 아직 연결 전이면 <see cref="NotLinkedException"/>.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_accessToken != null && DateTime.UtcNow < _expiresAt) return _accessToken;
        }

        var cred = Credentials.Load();
        if (!cred.HasClient) throw new NotLinkedException("OAuth 클라이언트가 설정되지 않았습니다");
        if (!cred.HasToken) throw new NotLinkedException("Google 계정 연결이 필요합니다");

        var form = new Dictionary<string, string>
        {
            ["client_id"] = cred.ClientId!,
            ["client_secret"] = cred.ClientSecret!,
            ["refresh_token"] = cred.RefreshToken!,
            ["grant_type"] = "refresh_token",
        };
        using var res = await _http.PostAsync(Config.OAuthTokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            // invalid_grant = 사용자가 접근 권한을 취소했거나 토큰이 만료됐다. 다시 연결해야 한다.
            if (body.Contains("invalid_grant", StringComparison.Ordinal))
            {
                Credentials.Clear();
                throw new NotLinkedException("Google 계정 연결이 끊어졌습니다. 다시 연결해 주세요");
            }
            throw new IOException($"토큰 갱신 실패 (HTTP {(int)res.StatusCode}): {Truncate(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString()
                    ?? throw new IOException("토큰 응답에 access_token 이 없습니다");
        var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;

        lock (_lock)
        {
            _accessToken = token;
            // 만료 직전에 쓰다가 401 을 맞지 않도록 1분 일찍 만료된 것으로 본다.
            _expiresAt = DateTime.UtcNow.AddSeconds(seconds - 60);
        }
        return token;
    }

    /// <summary>다음 호출에서 토큰을 새로 받게 한다 (401 을 맞았을 때).</summary>
    public void InvalidateAccessToken()
    {
        lock (_lock)
        {
            _accessToken = null;
            _expiresAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 브라우저를 띄워 동의를 받고 리프레시 토큰을 저장한다.
    /// 리디렉션은 이 PC 안에서만 오간다 (127.0.0.1 임시 포트).
    /// </summary>
    public async Task LinkAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        var port = FreePort();
        var redirect = $"http://127.0.0.1:{port}/";

        // PKCE. 설치형 앱은 시크릿이 사실상 공개되므로 코드 가로채기를 이걸로 막는다.
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var authUrl = Config.OAuthAuthEndpoint
                      + "?client_id=" + Uri.EscapeDataString(clientId)
                      + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                      + "&response_type=code"
                      + "&scope=" + Uri.EscapeDataString(Config.OAuthScope)
                      + "&code_challenge=" + challenge
                      + "&code_challenge_method=S256"
                      + "&state=" + state
                      // 리프레시 토큰은 동의 화면을 실제로 거칠 때만 내려온다.
                      + "&access_type=offline&prompt=consent";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        Log.Info("브라우저에서 Google 동의를 기다립니다: " + redirect);

        try
        {
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            listener.Stop();
            throw new IOException("브라우저를 열지 못했습니다: " + e.Message);
        }

        string code;
        try
        {
            // 5분 안에 안 돌아오면 포기한다.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var contextTask = listener.GetContextAsync();
            var finished = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeout.Token));
            if (finished != contextTask) throw new TimeoutException("동의를 기다리다 시간이 지났습니다");

            var context = await contextTask;
            var query = context.Request.QueryString;
            var error = query["error"];
            code = query["code"] ?? "";
            var gotState = query["state"];

            var ok = error == null && code.Length > 0 && gotState == state;
            await RespondAsync(context, ok
                ? "연결되었습니다. 이 창을 닫고 Life Recorder 로 돌아가세요."
                : "연결하지 못했습니다: " + (error ?? "응답이 올바르지 않습니다"));

            if (!ok) throw new IOException("동의를 받지 못했습니다: " + (error ?? "state 불일치"));
        }
        finally
        {
            listener.Stop();
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirect,
        };
        using var res = await _http.PostAsync(Config.OAuthTokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new IOException($"토큰 교환 실패 (HTTP {(int)res.StatusCode}): {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        var refresh = doc.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        if (string.IsNullOrEmpty(refresh))
            throw new IOException("리프레시 토큰이 오지 않았습니다. 계정의 앱 접근 권한을 지우고 다시 시도해 주세요");

        new Credentials { ClientId = clientId, ClientSecret = clientSecret, RefreshToken = refresh }.Save();
        InvalidateAccessToken();
        Log.Info("Google 계정 연결 완료");
    }

    public static void Unlink()
    {
        Credentials.Clear();
        Prefs.ClearFolderIds();
        Log.Info("Google 계정 연결 해제");
    }

    // ── 잡동사니 ─────────────────────────────────────────────────────────────

    private static async Task RespondAsync(HttpListenerContext context, string message)
    {
        var html = "<!doctype html><meta charset=\"utf-8\"><title>Life Recorder</title>"
                   + "<body style=\"font-family:system-ui;padding:3rem;line-height:1.6\"><p>"
                   + WebUtility.HtmlEncode(message) + "</p></body>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
