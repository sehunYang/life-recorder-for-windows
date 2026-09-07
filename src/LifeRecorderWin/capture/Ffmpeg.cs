namespace LifeRecorderWin.Capture;

/// <summary>
/// 번들한 ffmpeg 을 찾는다.
///
/// 저장소에는 바이너리를 넣지 않는다 (용량 + GPL 재배포).
/// <c>scripts\get-ffmpeg.ps1</c> 이 <c>tools\ffmpeg\ffmpeg.exe</c> 로 받아 두고,
/// 배포할 때는 실행 파일 옆 <c>ffmpeg\</c> 에 같이 넣는다.
/// </summary>
internal static class Ffmpeg
{
    private static string? _cached;

    public static string? Find()
    {
        if (_cached != null && File.Exists(_cached)) return _cached;

        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate))
            {
                _cached = candidate;
                return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var baseDir = AppContext.BaseDirectory;

        // 배포 배치: 실행 파일 옆.
        yield return Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe");
        yield return Path.Combine(baseDir, "ffmpeg.exe");

        // 개발 배치: 저장소 어딘가 위쪽의 tools\ffmpeg\.
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "tools", "ffmpeg", "ffmpeg.exe");

        // 마지막으로 PATH.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = p.Trim('"');
            if (trimmed.Length > 0) yield return Path.Combine(trimmed, "ffmpeg.exe");
        }
    }
}
