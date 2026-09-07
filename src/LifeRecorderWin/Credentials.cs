using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LifeRecorderWin;

/// <summary>
/// Google OAuth 클라이언트 정보와 리프레시 토큰.
///
/// DPAPI(<c>CurrentUser</c> 범위)로 암호화해 둔다. 이 PC의 이 사용자 계정으로 로그인해야만 풀린다.
/// 파일을 통째로 복사해 다른 PC에 붙여도 읽히지 않는다.
///
/// 안드로이드는 Play 서비스가 토큰을 대신 들고 있어서 앱이 보관할 게 없었지만,
/// 데스크톱에는 그런 게 없어 직접 보관한다.
/// </summary>
internal sealed class Credentials
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? RefreshToken { get; set; }

    public bool HasClient => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    public bool HasToken => !string.IsNullOrWhiteSpace(RefreshToken);

    private static string Path_ => System.IO.Path.Combine(Storage.BaseDir, "credentials.bin");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LifeRecorder.windows.v1");
    private static readonly object Lock = new();

    public static Credentials Load()
    {
        lock (Lock)
        {
            try
            {
                if (!File.Exists(Path_)) return new Credentials();
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(Path_), Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<Credentials>(plain) ?? new Credentials();
            }
            catch (Exception)
            {
                // 다른 계정에서 만든 파일이거나 깨진 파일. 다시 연결하면 된다.
                return new Credentials();
            }
        }
    }

    public void Save()
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Storage.BaseDir);
            var plain = JsonSerializer.SerializeToUtf8Bytes(this);
            var blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Path_, blob);
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            try { File.Delete(Path_); } catch (IOException) { }
        }
    }
}
