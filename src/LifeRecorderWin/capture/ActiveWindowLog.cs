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
/// 브라우저면 주소창 URL 과 문서 스크롤 위치도 같이 (<see cref="BrowserProbe"/>).
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

    private readonly MediaWatcher _media;
    private readonly BrowserProbe _probe = new();
    private System.Threading.Timer? _timer;
    private readonly Dictionary<int, string> _procNames = new();
    private string? _lastKey;
    private string? _lastMediaKey;
    private bool _lastFullscreen;
    private bool _idle;
    private int _busy;
    /// <summary>직전 틱의 주소창 값. 두 틱 연속 같을 때만 믿는다 (치는 중인 값을 거른다).</summary>
    private string? _seenUrl;
    private string? _scrollUrl;
    private double _scrollPos;

    public ActiveWindowLog(MediaWatcher media)
    {
        _media = media;
    }

    /// <summary>세션이 열릴 때. 첫 틱에서 지금 앞에 있는 창이 바로 적힌다.</summary>
    public void Start(string reason)
    {
        _lastKey = null;
        _lastMediaKey = null;
        _lastFullscreen = false;
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

    /// <summary>
    /// 미디어 상태를 새로 본 직후(10초마다). 재생 중인 세션이 바뀌었을 때만 한 줄 남긴다 —
    /// 무엇을 틀어 놓고 있었는지(앱·제목)가 글자로 남는다. 세션이 없으면 stopped 한 줄.
    /// </summary>
    public void OnMediaPolled()
    {
        if (_timer == null) return;
        try
        {
            var p = _media.Playing;
            var key = string.Join("", p.Select(s => s.App + "|" + s.Title + "|" + s.Artist));
            if (key != _lastMediaKey)
            {
                _lastMediaKey = key;
                if (p.Count == 0)
                    Write(new Dictionary<string, object?> { ["event"] = "media", ["state"] = "stopped" });
                else
                    foreach (var s in p)
                        Write(new Dictionary<string, object?>
                        {
                            ["event"] = "media", ["state"] = "playing",
                            ["app"] = s.App, ["title"] = s.Title, ["artist"] = s.Artist,
                        });
            }
            if (_media.Fullscreen != _lastFullscreen)
            {
                _lastFullscreen = _media.Fullscreen;
                Write(new Dictionary<string, object?> { ["event"] = "fullscreen", ["on"] = _lastFullscreen });
            }
        }
        catch (Exception e)
        {
            Log.Warn("미디어 기록 실패: " + e.Message);
        }
    }

    private void Tick()
    {
        // 브라우저 트리를 처음 뒤질 때 1초를 넘길 수 있다. 겹쳐 돌지 않게 한다.
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var idle = Input.IdleMs() >= Config.AppIdleAfterMs;
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

            string? url = null;
            if (Config.BrowserProcs.Contains(proc, StringComparer.OrdinalIgnoreCase))
            {
                var raw = _probe.Url(h);
                var stable = raw == _seenUrl;
                _seenUrl = raw;
                // 주소창이 바뀌는 중(새 페이지로 가는 중·검색어를 치는 중)이면 다음 틱에 적는다.
                if (!stable) return;
                url = raw;
            }
            else
            {
                _seenUrl = null;
            }

            var key = proc + "" + title + "" + url;
            if (key != _lastKey)
            {
                _lastKey = key;
                var rec = new Dictionary<string, object?>
                {
                    ["event"] = "focus",
                    ["proc"] = proc,
                    ["pid"] = (int)pid,
                    ["title"] = title,
                };
                if (url != null) rec["url"] = url;
                Write(rec);
            }

            if (url != null) NoteScroll(h, url);
        }
        catch (Exception e)
        {
            Log.Warn("앞 창 기록 실패: " + e.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// 문서 스크롤 위치가 한 칸(<see cref="Config.AppScrollStep"/>) 넘게 움직였을 때만 한 줄.
    /// 새 페이지는 0에서 시작한 것으로 본다. 읽은 리듬(조금씩·멈춤)과 깊이(끝까지 갔나)가 남는다.
    /// </summary>
    private void NoteScroll(IntPtr h, string url)
    {
        var s = _probe.Scroll(h, url);
        if (s == null) return;
        if (url != _scrollUrl)
        {
            _scrollUrl = url;
            _scrollPos = 0;
        }
        var (pos, view) = s.Value;
        if (Math.Abs(pos - _scrollPos) < Config.AppScrollStep) return;
        _scrollPos = pos;
        Write(new Dictionary<string, object?>
        {
            ["event"] = "scroll",
            ["url"] = url,
            ["pos"] = Math.Round(pos, 2),
            ["view"] = Math.Round(view, 2),
        });
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
                File.AppendAllText(path, JsonSerializer.Serialize(record, Storage.JsonlOptions) + "\n", Encoding.UTF8);
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

    public void Dispose() => Stop("종료");

    // ── Win32 ────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
}
