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
///           pcapp_&lt;날짜&gt;_&lt;기기&gt;_h&lt;시&gt;.jsonl  확정된 한 시간치 앞 창 기록, 업로드 대상 (화면 글자도 같다)
///   index\  rawpcindex_&lt;날짜&gt;.jsonl.part  오늘치 수집 기록 (계속 이어 쓰는 중, 업로드 대상 아님)
///           rawpcapp_&lt;날짜&gt;_&lt;기기&gt;_h&lt;시&gt;.jsonl.part  지금 시간치 앞 창 기록 (같음)
/// </code>
///
/// 업로더는 <c>queue\</c> 만 본다. 그래서 쓰는 중인 파일이 올라갈 일이 없다.
/// 안드로이드는 같은 목적을 <c>.part</c> 접미어로 풀었지만, 여기서는 ffmpeg 이 파일 이름을 정하므로
/// 이름 대신 폴더로 가른다.
/// </summary>
internal static class Storage
{
    public const string Part = ".part";

    /// <summary>
    /// JSONL 한 줄을 쓸 때. 기본값은 한글을 녹화 로 이스케이프해 파일이 여섯 배로 불고 사람이 못 읽는다.
    /// 안드로이드 쪽처럼 UTF-8 원문 그대로 쓴다. 파일은 우리만 읽으니 HTML 안전성은 필요 없다.
    /// </summary>
    public static readonly System.Text.Json.JsonSerializerOptions JsonlOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// JSONL 파일에 쓸 인코딩. <c>Encoding.UTF8</c> 은 파일을 새로 만들 때 BOM(EF BB BF)을 앞에 붙여
    /// 첫 줄이 JSON 으로 안 읽힌다 (2026-09-18 실측: pcapp_·pcindex_ 첫 줄이 그랬다). BOM 없이 쓴다.
    /// </summary>
    public static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

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
        name.StartsWith(Config.IndexPrefix, StringComparison.Ordinal) ? "index" :
        name.StartsWith(Config.AppPrefix, StringComparison.Ordinal) ? "app" :
        name.StartsWith(Config.ScreenTextPrefix, StringComparison.Ordinal) ? "screentext" :
        "screen";

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
    //   pcapp_2026-10-01_home_h13.jsonl      ← 한 시간 조각은 기기 이름 뒤에 시(時)를 붙인다

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
    /// 조각 키(<see cref="HourSlice"/>)를 파일 이름 몸통으로. 날짜·기기·시 순서다.
    /// <c>2026-10-01_h13</c> → <c>2026-10-01_home_h13</c>, 하루 키 <c>2026-09-30</c> → <c>2026-09-30_home</c>.
    /// </summary>
    private static string SliceStem(string key) =>
        key.Length > 10 ? $"{key[..10]}_{DeviceName}{key[10..]}" : $"{key}_{DeviceName}";

    /// <summary>확정된 앞 창 기록 (업로드 대상). 키가 한 시간이면 한 시간치, 날짜면 하루치.</summary>
    public static string AppName(string key) => $"{Config.AppPrefix}{SliceStem(key)}.jsonl";

    /// <summary>지금 쓰고 있는 앞 창 기록.</summary>
    public static string RawAppName(string key) => $"{Config.RawAppPrefix}{SliceStem(key)}.jsonl{Part}";

    /// <summary>확정된 화면 글자 (업로드 대상). 키가 한 시간이면 한 시간치, 날짜면 하루치.</summary>
    public static string ScreenTextName(string key) => $"{Config.ScreenTextPrefix}{SliceStem(key)}.jsonl";

    /// <summary>지금 쓰고 있는 화면 글자.</summary>
    public static string RawScreenTextName(string key) => $"{Config.RawScreenTextPrefix}{SliceStem(key)}.jsonl{Part}";

    /// <summary>
    /// <c>rawpcindex_2026-09-08_home.jsonl.part</c> 같은 이름에서 날짜만 꺼낸다. 형식이 아니면 null.
    /// 기기 이름을 바꾼 뒤에도 예전 이름의 파일을 확정할 수 있어야 해서 이름은 보지 않는다.
    /// </summary>
    public static string? DayFromRawName(string rawPrefix, string fileName)
    {
        if (!fileName.StartsWith(rawPrefix, StringComparison.Ordinal)) return null;
        var rest = fileName[rawPrefix.Length..];
        if (rest.Length < 10) return null;
        var day = rest[..10];
        return DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _) ? day : null;
    }

    /// <summary>
    /// <c>index\</c> 에 쌓인 오늘치 <c>.jsonl.part</c> 중 날이 지난 것을 <c>queue\</c> 로 옮겨 확정한다.
    /// 내용은 손대지 않는다. 수집 기록과 앞 창 기록이 같은 규칙을 쓴다.
    ///
    /// 기기 이름을 바꿨다면 예전 이름으로 쌓인 것도 지금 이름으로 확정된다.
    /// 어차피 같은 컴퓨터가 남긴 것이고, 이름이 둘로 갈리면 파일만 늘어난다.
    /// 호출하는 쪽이 자기 잠금을 잡고 부른다.
    /// </summary>
    /// <returns>확정한 파일 수</returns>
    public static int FinalizeDailyRaw(string rawPrefix, Func<string, string> doneNameOf)
    {
        var today = Today();
        var count = 0;
        foreach (var f in new DirectoryInfo(IndexDir).GetFiles(rawPrefix + "*.jsonl" + Part))
        {
            var day = DayFromRawName(rawPrefix, f.Name);
            if (day == null || string.CompareOrdinal(day, today) >= 0) continue;
            if (f.Length == 0)
            {
                try { f.Delete(); } catch (IOException) { }
                continue;
            }

            if (MoveToQueue(f, doneNameOf(day), rawPrefix + day)) count++;
        }
        return count;
    }

    /// <summary>
    /// <c>rawpcapp_2026-10-01_home_h13.jsonl.part</c> 같은 이름에서 조각 키(<c>2026-10-01_h13</c>)를 꺼낸다.
    /// 예전 판이 남긴 <c>rawpcapp_2026-09-30_home.jsonl.part</c> 면 날짜(<c>2026-09-30</c>)만. 형식이 아니면 null.
    /// 기기 이름에는 밑줄이 없으므로 날짜 뒤 둘째 밑줄이 있을 때만 시(時)로 읽는다.
    /// </summary>
    public static string? SliceKeyFromRawName(string rawPrefix, string fileName)
    {
        var day = DayFromRawName(rawPrefix, fileName);
        if (day == null) return null;
        var m = SlicedRawPattern.Match(fileName[(rawPrefix.Length + 10)..]);
        if (!m.Success) return null;
        return m.Groups[1].Success ? day + "_h" + m.Groups[1].Value : day;
    }

    /// <summary>날짜 뒤의 꼬리. <c>_&lt;기기&gt;[_hHH].jsonl.part</c></summary>
    private static readonly System.Text.RegularExpressions.Regex SlicedRawPattern =
        new(@"^_[^_]*(?:_h(\d{2}))?\.jsonl\.part$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>index\</c> 에 쌓인 <c>.jsonl.part</c> 중 닫힌 조각을 <c>queue\</c> 로 옮겨 확정한다. 앞 창 기록·화면 글자가 쓴다.
    /// 한 시간 조각은 정각 1분 뒤(<see cref="HourSlice.ClosedBefore"/>), 예전 판이 남긴 하루치는 날이 지나면 닫힌다.
    /// 나머지는 <see cref="FinalizeDailyRaw"/> 와 같다. 호출하는 쪽이 자기 잠금을 잡고 부른다.
    /// </summary>
    /// <returns>확정한 파일 수</returns>
    public static int FinalizeSlicedRaw(string rawPrefix, Func<string, string> doneNameOf)
    {
        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var closedBefore = HourSlice.ClosedBefore(now);
        var count = 0;
        foreach (var f in new DirectoryInfo(IndexDir).GetFiles(rawPrefix + "*.jsonl" + Part))
        {
            var key = SliceKeyFromRawName(rawPrefix, f.Name);
            if (key == null) continue;
            var closed = key.Length == 10
                ? string.CompareOrdinal(key, today) < 0             // 하루치 (예전 판)
                : string.CompareOrdinal(key, closedBefore) < 0;     // 한 시간치 2026-10-01_h13
            if (!closed) continue;
            if (f.Length == 0)
            {
                try { f.Delete(); } catch (IOException) { }
                continue;
            }
            if (MoveToQueue(f, doneNameOf(key), rawPrefix + key)) count++;
        }
        return count;
    }

    /// <summary>
    /// 확정 한 건. 대기열에 같은 이름이 아직 있으면(업로드가 밀렸다) 뒤에 이어 붙이고 원본을 지운다.
    /// </summary>
    private static bool MoveToQueue(FileInfo f, string doneName, string label)
    {
        var dest = Path.Combine(QueueDir, doneName);
        try
        {
            if (File.Exists(dest))
            {
                // 이미 확정된 조각에 뒤늦게 더 붙은 경우 (업로드가 밀려 아직 대기열에 있다). 이어 붙인다.
                using (var src = f.OpenRead())
                using (var dst = new FileStream(dest, FileMode.Append, FileAccess.Write))
                    src.CopyTo(dst);
                f.Delete();
            }
            else
            {
                f.MoveTo(dest);
            }
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"{label} 확정 실패: {e.Message}");
            return false;
        }
    }
}
