using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace LifeRecorderWin.Capture;

/// <summary>
/// 앞 창에 보이는 글자를 앱을 가리지 않고 그대로 남긴다. 안드로이드 <c>ScreenTextService.kt</c> 와 같은 자리다.
///
/// 화면 mp4 에서 글자를 다시 읽으려면 OCR 이 필요하다. Windows UI 자동화는 창의 요소 트리를 글자로
/// 주므로 OCR 없이 원문이 남는다 — 크롬 웹 본문, 오피스, 탐색기, VS Code, 카카오톡 PC 가 그렇다.
/// 그림으로 그리는 것(게임·영상)과 트리를 내놓지 않는 앱은 안 온다. 그건 mp4 가 맡는다.
///
/// 무엇을 버릴지는 여기서 정하지 않는다. 내려받은 쪽이 정한다. 예외는 셋이다 —
/// 비밀번호 입력란(<c>IsPassword</c>)과 이 앱 자신의 창, 그리고 가린 창(Brave·Chrome 시크릿, <see cref="PrivateWindows"/>). 사람이 없으면(입력 60초 없음) 읽지 않는다.
///
/// <code>
///   index\ rawpcscreentext_yyyy-MM-dd_&lt;기기&gt;_hHH.jsonl.part  ← 지금 시간치 (업로드 대상 아님)
///   queue\ pcscreentext_yyyy-MM-dd_&lt;기기&gt;_hHH.jsonl          ← 정각 1분 뒤 확정된 한 시간치, 업로드 대상
/// </code>
/// 앞 창 기록과 같은 규칙이다 (<see cref="HourSlice"/>).
///
/// 폰과 같은 규칙: 1.5초마다 보되 **새로 나타난 글자 노드만** 적는다(같은 창에서 2분 안에 본 것은 다시 안 넣음),
/// 입력란은 두 번 연속 같을 때만(타자 중간 상태 제외), 진행 막대는 뺀다.
/// 큰 트리(웹 페이지)는 한 번에 1초 넘게 걸릴 수 있어, 오래 걸린 창은 다음 읽기를 늦춘다.
/// </summary>
internal sealed class ScreenTextLog : IDisposable
{
    private static readonly object Lock = new();
    private static readonly int SelfPid = Environment.ProcessId;
    /// <summary>시험용. 사람이 없어도 읽는다 (하네스에서 손을 안 대고 돌릴 때).</summary>
    internal static bool IgnoreIdle;
    /// <summary>시험용. 왜 안 읽었는지·오류를 밖으로 알린다.</summary>
    internal static Action<string>? Trace;

    private System.Threading.Timer? _timer;
    private int _busy;
    /// <summary>최근 본 노드(창·글·위치 → 본 시각). 새로 나타난 것만 남기기 위한 기억.</summary>
    private readonly Dictionary<string, long> _recent = new();
    /// <summary>입력란(창·요소 → 직전 스캔의 글). 두 번 연속 같을 때만 남긴다.</summary>
    private readonly Dictionary<string, string> _editing = new();
    private readonly Dictionary<int, string> _procNames = new();
    private string? _lastKey;
    private long _nextScanAt;
    private long _scanCount, _nodeCount, _slowCount;

    private static readonly CacheRequest Cache = BuildCache();

    private static CacheRequest BuildCache()
    {
        // 요소마다 속성을 따로 물으면 프로세스 경계를 수천 번 넘는다. 한 번에 받아 온다.
        var cr = new CacheRequest { AutomationElementMode = AutomationElementMode.None, TreeScope = TreeScope.Element };
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.ControlTypeProperty);
        cr.Add(AutomationElement.BoundingRectangleProperty);
        cr.Add(AutomationElement.IsOffscreenProperty);
        cr.Add(AutomationElement.IsPasswordProperty);
        cr.Add(AutomationElement.AutomationIdProperty);
        cr.Add(AutomationElement.IsValuePatternAvailableProperty);
        cr.Add(ValuePattern.ValueProperty);
        return cr;
    }

    public void Start(string reason)
    {
        _lastKey = null;
        _nextScanAt = 0;
        Note("start", reason);
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => Tick(), null, 500, Config.ScreenTextPollMs);
    }

    public void Stop(string reason)
    {
        var t = _timer;
        _timer = null;
        if (t == null) return;
        t.Dispose();
        Note("stop", reason + $" (읽기 {_scanCount}회 · 노드 {_nodeCount} · 느린 창 {_slowCount})");
        _scanCount = _nodeCount = _slowCount = 0;
    }

    private void Note(string evt, string? reason) =>
        Write(new Dictionary<string, object?> { ["kind"] = "service", ["event"] = evt, ["reason"] = reason });

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var now = Environment.TickCount64;
            if (now < _nextScanAt) { Trace?.Invoke("backoff"); return; }
            if (!IgnoreIdle && Input.IdleMs() >= Config.AppIdleAfterMs) { Trace?.Invoke("idle"); return; }     // 사람이 없다. 화면은 안 바뀐다
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) { Trace?.Invoke("no foreground"); return; }
            GetWindowThreadProcessId(h, out var pidU);
            var pid = (int)pidU;
            if (pid == SelfPid) { Trace?.Invoke("self"); return; }
            if (PrivateWindows.IsPrivate(h)) { Trace?.Invoke("private"); _lastKey = null; return; }   // Brave·Chrome 시크릿은 한 글자도 안 남긴다
            var proc = ProcName(pid);
            var title = Title(h);

            var sw = Stopwatch.StartNew();
            var nodes = Scan(h, proc, title, now);
            sw.Stop();
            _scanCount++;
            if (sw.ElapsedMilliseconds > Config.ScreenTextBudgetMs)
            {
                // 큰 트리. 다음 읽기를 늦춰 CPU 를 아낀다 (최대 30초).
                _slowCount++;
                var wait = Math.Min(30_000, (int)sw.ElapsedMilliseconds * 4);
                _nextScanAt = Environment.TickCount64 + wait;
            }

            var key = proc + "" + title;
            if (nodes.Count == 0 && key == _lastKey) return;
            _lastKey = key;
            _nodeCount += nodes.Count;
            Write(new Dictionary<string, object?>
            {
                ["kind"] = "screen",
                ["proc"] = proc,
                ["pid"] = pid,
                ["title"] = title,
                ["ms"] = (int)sw.ElapsedMilliseconds,
                ["nodes"] = nodes,
            });
        }
        catch (Exception e)
        {
            Log.Warn("화면 글자 기록 실패: " + e.Message);
            Trace?.Invoke("error: " + e);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private List<Dictionary<string, object?>> Scan(IntPtr h, string proc, string title, long now)
    {
        Expire(now);
        var nodes = new List<Dictionary<string, object?>>();
        AutomationElementCollection all;
        // 창 요소는 캐시 밖에서 만든다. AutomationElementMode.None 인 캐시 안에서 만들면 네이티브 핸들이 없어
        // FindAll 이 "SafeHandle cannot be null" 로 죽는다 (2026-09-18 실측).
        var root = AutomationElement.FromHandle(h);
        using (Cache.Activate())
        {
            all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        }
        var visited = 0;
        foreach (AutomationElement el in all)
        {
            if (++visited > Config.ScreenTextMaxNodes) break;
            string? text;
            ControlType ct;
            System.Windows.Rect rect;
            bool offscreen, password, hasValue;
            string aid;
            try
            {
                if (el.Cached.IsOffscreen) continue;
                ct = el.Cached.ControlType;
                offscreen = false;
                password = el.Cached.IsPassword;
                rect = el.Cached.BoundingRectangle;
                aid = el.Cached.AutomationId ?? "";
                hasValue = el.GetCachedPropertyValue(AutomationElement.IsValuePatternAvailableProperty) is true;
                text = el.Cached.Name;
                if (hasValue && !password)
                {
                    var v = el.GetCachedPropertyValue(ValuePattern.ValueProperty) as string;
                    // 입력란·문서는 Name 이 라벨("주소창")이고 Value 가 내용이다. 내용이 있으면 내용을 쓴다.
                    if (!string.IsNullOrWhiteSpace(v) && (ct == ControlType.Edit || ct == ControlType.Document || string.IsNullOrWhiteSpace(text)))
                        text = v;
                }
            }
            catch (Exception)
            {
                continue;   // 요소가 그새 사라졌다
            }
            _ = offscreen;
            // 비밀번호 칸은 시스템이 가린 채로 주지만 그마저 남기지 않는다. 진행 막대·슬라이더는 상태지 내용이 아니다.
            if (password || ct == ControlType.ProgressBar || ct == ControlType.Slider) continue;
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
            if (text.Length > Config.ScreenTextTextMax) text = text[..Config.ScreenTextTextMax];

            var cls = ct.ProgrammaticName.Replace("ControlType.", "");
            if (ct == ControlType.Edit || ct == ControlType.Document)
            {
                // 타자 치는 중간 상태를 거른다. 직전 스캔과 같을 때만(= 멈췄을 때) 남긴다.
                var ek = proc + "" + aid + "" + (int)rect.Left + "," + (int)rect.Top;
                _editing.TryGetValue(ek, out var prev);
                _editing[ek] = text;
                if (prev != text) continue;
            }
            var l = (int)rect.Left; var t = (int)rect.Top; var r = (int)rect.Right; var b = (int)rect.Bottom;
            var key = $"{proc}{title}{aid}{text}{l},{t},{r},{b}";
            if (_recent.ContainsKey(key)) { _recent[key] = now; continue; }
            _recent[key] = now;
            nodes.Add(new Dictionary<string, object?>
            {
                ["aid"] = aid.Length == 0 ? null : aid,
                ["cls"] = cls,
                ["text"] = text,
                ["l"] = l, ["t"] = t, ["r"] = r, ["b"] = b,
            });
        }
        return nodes;
    }

    private void Expire(long now)
    {
        if (_editing.Count > 500) _editing.Clear();
        if (_recent.Count < Config.ScreenTextMaxRecent && _recent.Count % 200 != 0) return;
        foreach (var k in _recent.Where(kv => now - kv.Value > Config.ScreenTextRecentTtlMs).Select(kv => kv.Key).ToList())
            _recent.Remove(k);
        if (_recent.Count > Config.ScreenTextMaxRecent)
            foreach (var k in _recent.OrderBy(kv => kv.Value).Take(_recent.Count - Config.ScreenTextMaxRecent).Select(kv => kv.Key).ToList())
                _recent.Remove(k);
    }

    private void Write(Dictionary<string, object?> fields)
    {
        var now = DateTimeOffset.Now;
        var record = new Dictionary<string, object?> { ["t"] = now.ToUnixTimeMilliseconds() };
        foreach (var (k, v) in fields) record[k] = v;
        record["device"] = Storage.DeviceName;
        lock (Lock)
        {
            try
            {
                var path = Path.Combine(Storage.IndexDir, Storage.RawScreenTextName(HourSlice.Key(now.LocalDateTime)));
                File.AppendAllText(path, JsonSerializer.Serialize(record, Storage.JsonlOptions) + "\n", Storage.Utf8NoBom);
            }
            catch (Exception e)
            {
                Log.Warn("화면 글자 append 실패: " + e.Message);
            }
        }
    }

    /// <summary>닫힌 한 시간치(예전 판의 하루치는 날이 지난 것)를 업로드 대상으로 확정한다.</summary>
    public static int FinalizeCompleted()
    {
        lock (Lock) return Storage.FinalizeSlicedRaw(Config.RawScreenTextPrefix, Storage.ScreenTextName);
    }

    private string ProcName(int pid)
    {
        if (_procNames.TryGetValue(pid, out var cached)) return cached;
        if (_procNames.Count > 512) _procNames.Clear();
        string name;
        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
        catch (Exception) { name = "?"; }
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
}
