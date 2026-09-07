using System.Globalization;
using System.Security.Cryptography;

namespace LifeRecorderWin;

/// <summary>
/// 파일 배치와 이름 규칙. 안드로이드 <c>Storage.kt</c> 와 같은 자리다.
///
/// <code>
///   work\   pcscreen_&lt;시각&gt;.mp4     ffmpeg 이 지금 쓰고 있는(또는 방금 닫은) 세그먼트
///   queue\  pcscreen_&lt;시각&gt;.mp4     완성된 업로드 대상
///           pcindex_&lt;날짜&gt;.jsonl     확정된 수집 기록, 업로드 대상
///   index\  rawpcindex_&lt;날짜&gt;.jsonl.part  오늘치 수집 기록 (계속 이어 쓰는 중, 업로드 대상 아님)
/// </code>
///
/// 업로더는 <c>queue\</c> 만 본다. 그래서 쓰는 중인 파일이 올라갈 일이 없다.
/// 안드로이드는 같은 목적을 <c>.part</c> 접미어로 풀었지만, 여기서는 ffmpeg 이 파일 이름을 정하므로
/// 이름 대신 폴더로 가른다.
/// </summary>
internal static class Storage
{
    public const string Part = ".part";

    public static string BaseDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LifeRecorder");

    public static string WorkDir => Ensure(Path.Combine(BaseDir, "work"));
    public static string QueueDir => Ensure(Path.Combine(BaseDir, "queue"));
    public static string IndexDir => Ensure(Path.Combine(BaseDir, "index"));
    public static string LogDir => Ensure(Path.Combine(BaseDir, "logs"));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>업로드 대기 중인 완성 파일. 오래된 것부터.</summary>
    public static List<FileInfo> FinishedFiles()
    {
        var dir = new DirectoryInfo(QueueDir);
        return dir.GetFiles()
            .Where(f => !f.Name.EndsWith(Part, StringComparison.Ordinal) && f.Length > 0)
            // 접두어를 뗀 시각 부분으로 정렬해 종류가 시간순으로 섞이게 한다 (안드로이드와 같은 규칙).
            .OrderBy(f => f.Name[(f.Name.IndexOf('_') + 1)..], StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>파일 이름 접두어로 Drive 폴더 키를 정한다.</summary>
    public static string FolderKeyOf(string name) =>
        name.StartsWith(Config.IndexPrefix, StringComparison.Ordinal) ? "index" : "screen";

    public static string MimeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".jsonl" => "application/x-ndjson",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };

    /// <summary>사람이 읽는 파일 크기.</summary>
    public static string FmtBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):F1} MB",
        > 0 => $"{b / 1024} KB",
        _ => "0",
    };

    public static string Md5Hex(string path)
    {
        using var md5 = MD5.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024);
        return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// ffmpeg 이 도는 중에 호출한다. <c>work\</c> 안에서 **가장 최근 것 하나를 뺀** 나머지는
    /// 이미 닫힌 세그먼트이므로 업로드 대기열로 옮긴다.
    /// (가장 최근 것이 지금 쓰고 있는 파일이다)
    /// </summary>
    /// <returns>옮긴 파일 이름들</returns>
    public static List<string> PromoteClosedSegments()
    {
        var files = new DirectoryInfo(WorkDir).GetFiles(Config.ScreenPrefix + "*.mp4")
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
        var moved = new List<string>();
        // 마지막 하나는 쓰는 중이니 건드리지 않는다.
        for (var i = 0; i < files.Count - 1; i++)
        {
            if (TryPromote(files[i])) moved.Add(files[i].Name);
        }
        return moved;
    }

    /// <summary>
    /// ffmpeg 이 멈춘 상태에서 호출한다. <c>work\</c> 에 남은 것을 전수 검사해서
    /// <c>moov</c> 가 있으면(= 정상적으로 닫혔으면) 대기열로 올리고, 없으면 지운다.
    ///
    /// 안드로이드도 완성되지 않은 화면 세그먼트는 복구하지 않고 버린다. moov 가 없으면 재생할 수 없다.
    /// </summary>
    /// <returns>(살린 개수, 버린 개수)</returns>
    public static (int promoted, int dropped) RecoverWorkDir()
    {
        var promoted = 0;
        var dropped = 0;
        foreach (var f in new DirectoryInfo(WorkDir).GetFiles())
        {
            if (f.Length > 0 && Mp4.HasMoov(f.FullName))
            {
                if (TryPromote(f)) promoted++;
            }
            else
            {
                try { f.Delete(); dropped++; } catch (IOException) { /* 아직 잡혀 있으면 다음에 */ }
            }
        }
        return (promoted, dropped);
    }

    private static bool TryPromote(FileInfo f)
    {
        var dest = Path.Combine(QueueDir, f.Name);
        try
        {
            if (File.Exists(dest)) File.Delete(dest);
            f.MoveTo(dest);
            return true;
        }
        catch (IOException)
        {
            // ffmpeg 이 아직 핸들을 놓지 않았다. 다음 차례에 다시 시도한다.
            return false;
        }
    }

    /// <summary>수집 기록에 쓰는 날짜.</summary>
    public static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ── 기기 이름과 파일 이름 ────────────────────────────────────────────────
    //
    // 컴퓨터가 두 대 이상이면 이름이 겹친다. 특히 하루 한 개인 수집 기록은 **매일** 겹친다.
    // 그래서 시각 뒤에 기기 이름을 붙인다. 시각이 앞에 있어야 이름순 정렬이 곧 시간순이다.
    //
    //   pcscreen_2026-09-08_13-00-00_home.mp4
    //   pcindex_2026-09-08_home.jsonl

    /// <summary>파일 이름에 넣을 수 있는 형태로 다듬는다. 영숫자와 하이픈만 남긴다.</summary>
    public static string SanitizeDeviceName(string raw)
    {
        var chars = raw.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray();
        var s = new string(chars).Trim('-');
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-");
        return s.Length > Config.DeviceNameMaxLength ? s[..Config.DeviceNameMaxLength].Trim('-') : s;
    }

    /// <summary>정해져 있으면 기기 이름, 아니면 빈 문자열.</summary>
    public static string DeviceName => SanitizeDeviceName(Prefs.Current.DeviceName);

    public static bool HasDeviceName => DeviceName.Length > 0;

    /// <summary>ffmpeg 의 <c>-strftime</c> 에 넘길 출력 패턴.</summary>
    public static string ScreenPattern() =>
        Path.Combine(WorkDir, $"{Config.ScreenPrefix}%Y-%m-%d_%H-%M-%S_{DeviceName}.mp4");

    /// <summary>확정된 하루치 수집 기록 (업로드 대상).</summary>
    public static string IndexName(string day) => $"{Config.IndexPrefix}{day}_{DeviceName}.jsonl";

    /// <summary>오늘치 수집 기록 (계속 이어 쓰는 중).</summary>
    public static string RawIndexName(string day) => $"{Config.RawIndexPrefix}{day}_{DeviceName}.jsonl{Part}";

    /// <summary>
    /// <c>rawpcindex_2026-09-08_home.jsonl.part</c> 에서 날짜만 꺼낸다. 형식이 아니면 null.
    /// 기기 이름을 바꾼 뒤에도 예전 이름의 파일을 확정할 수 있어야 해서 이름은 보지 않는다.
    /// </summary>
    public static string? DayFromRawIndexName(string fileName)
    {
        if (!fileName.StartsWith(Config.RawIndexPrefix, StringComparison.Ordinal)) return null;
        var rest = fileName[Config.RawIndexPrefix.Length..];
        if (rest.Length < 10) return null;
        var day = rest[..10];
        return DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _) ? day : null;
    }
}
