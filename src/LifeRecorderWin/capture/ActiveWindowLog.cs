using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LifeRecorderWin.Capture;

/// <summary>
/// 어느 창이 앞에 있었는지를 남긴다. 화면 mp4 옆에 놓이는 색인이다.
///
/// 영상만 있으면 "이 시간에 무슨 앱을 썼나"를 알려고 프레임을 읽어야(OCR) 한다.
/// 1초에 한 번 앞 창의 프로세스 이름과 제목을 보고, **바뀌었을 때만** 한 줄 적는다.
/// 입력이 한동안 없으면 idle, 다시 있으면 active 를 적어 자리를 비운 구간도 남긴다.
/// 녹화 세션과 같이 켜지고 꺼지므로, 잠금·모니터 꺼짐 동안은 영상처럼 비어 있다.
///
/// <code>
///   index\ rawpcapp_yyyy-MM-dd_&lt;기기&gt;.jsonl.part  ← 오늘치 (업로드 대상 아님)
///   queue\ pcapp_yyyy-MM-dd_&lt;기기&gt;.jsonl          ← 날이 바뀌어 확정된 것, 업로드 대상
/// </code>
///
/// 해석하지 않는다. 어느 앱이 "의미 있는" 앱인지는 내려받은 쪽이 정한다.
/// 안드로이드의 <c>app_&lt;날짜&gt;.jsonl</c>(UsageEvents)과 같은 Drive 폴더에 올라간다.
/// </summary>
internal sealed class ActiveWindowLog : IDisposable
{
    private static readonly object Lock = new();

    private System.Threading.Timer? _timer;
    private readonly Dictionary<int, string> _procNames = new();
    private string? _lastKey;
    private bool _idle;

    /// <summary>세션이 열릴 때. 첫 틱에서 지금 앞에 있는 창이 바로 적힌다.</summary>
    public void Start(string reason)
    {
        _lastKey = null;
        _idle = false;
        Note("start", reason);
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => Tick(), null, 0, Config.AppPollMs);
    }

    /// <summary>세션이 닫힐 때. 왜 닫혔는지(OFF·잠금·ffmpeg 종료)를 같이 남긴다.</summary>
    public void Stop(string reason)
    {
        var t = _timer;
        _timer = null;
        if (t == null) return;
        t.Dispose();
        Note("stop", reason);
    }

    private void Note(string evt, string? reason) =>
        Write(new Dictionary<string, object?> { ["event"] = evt, ["reason"] = reason });

    private void Tick()
    {
        try
        {
            var idle = IdleMs() >= Config.AppIdleAfterMs;
            if (idle != _idle)
            {
                _idle = idle;
                Write(new Dictionary<string, object?> { ["event"] = idle ? "idle" : "active" });
            }

            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return;
            GetWindowThreadProcessId(h, out var pid);
            var proc = ProcName((int)pid);
            var title = Title(h);
            var key = proc + "" + title;
            if (key == _lastKey) return;
            _lastKey = key;
            Write(new Dictionary<string, object?>
            {
                ["event"] = "focus",
                ["proc"] = proc,
                ["pid"] = (int)pid,
                ["title"] = title,
            });
        }
        catch (Exception e)
        {
            Log.Warn("앞 창 기록 실패: " + e.Message);
        }
    }

    private void Write(Dictionary<string, object?> fields)
    {
        var record = new Dictionary<string, object?>
        {
            ["t"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            ["kind"] = "app",
        };
        foreach (var (k, v) in fields) record[k] = v;
        record["device"] = Storage.DeviceName;

        lock (Lock)
        {
            try
            {
                var path = Path.Combine(Storage.IndexDir, Storage.RawAppName(Storage.Today()));
                File.AppendAllText(path, JsonSerializer.Serialize(record) + "\n", Encoding.UTF8);
            }
            catch (Exception e)
            {
                Log.Warn("앞 창 기록 append 실패: " + e.Message);
            }
        }
    }

    /// <summary>날이 지난 기록을 업로드 대상으로 확정한다. 수집 기록과 같은 규칙.</summary>
    public static int FinalizeCompletedDays()
    {
        lock (Lock) return Storage.FinalizeDailyRaw(Config.RawAppPrefix, Storage.AppName);
    }

    /// <summary>
    /// pid → 프로세스 이름. 같은 창을 1초마다 보므로 캐시한다.
    /// pid 가 재사용되면 틀릴 수 있지만 몇 초 안에 일어나는 일은 아니고, 커지면 비운다.
    /// </summary>
    private string ProcName(int pid)
    {
        if (_procNames.TryGetValue(pid, out var cached)) return cached;
        if (_procNames.Count > 512) _procNames.Clear();
        string name;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
        }
        catch (Exception)
        {
            name = "?";
        }
        _procNames[pid] = name;
        return name;
    }

    private static string Title(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        var s = sb.ToString();
        return s.Length > Config.AppTitleMaxLength ? s[..Config.AppTitleMaxLength] : s;
    }

    /// <summary>마지막 입력 뒤 지난 시간. 틱 카운트가 넘쳐도 unsigned 뺄셈이라 맞다.</summary>
    private static uint IdleMs()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime);
    }

    public void Dispose() => Stop("종료");

    // ── Win32 ────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
