using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace LifeRecorderWin.Capture;

/// <summary>
/// 기록에 남기지 않을 창을 가려낸다 — Brave 의 모든 창, Chrome 의 시크릿 창.
/// 화면 영상에서는 이 창이 보이는 부분을 검게 칠하고(<see cref="ScreenGrabber"/>),
/// 앞 창 기록·화면 글자·미디어 제목에서는 내용을 빼고 "가린 창"이라고만 남긴다.
///
/// Chrome 시크릿 창은 일반 창과 같은 chrome.exe 이고 창 제목(GetWindowText)도 같다.
/// 다른 점은 UI 자동화 트리뿐이다 — 창 바로 아래 <c>BrowserRootView</c> 의 이름이 (2026-09-26 실측)
/// <code>
///   일반:   "&lt;제목&gt; - Chrome - &lt;프로필&gt;"   (프로필 이름에 괄호가 있을 수 있다: "세훈 (gclass)")
///   시크릿: "&lt;제목&gt; - Chrome (시크릿 모드)"     (영어판 "(Incognito)". 언어마다 괄호 안만 다르다)
///   ("Chrome" 앞에 "Google " 이 붙는 판도 있다)
/// </code>
///
/// **모르면 가린다.** 일반 창이라는 확인(위 모양)이 있어야만 드러낸다. 묻는 중·오류·처음 보는 모양은 전부 가린다.
/// UI 자동화는 창마다 한 번 묻고 창이 없어질 때까지 들고 있다 — 최소화·다른 데스크톱으로 가도 잊지 않는다.
/// 녹화 스레드는 UI 자동화를 기다리지 않는다. 가린 채로 두고 스레드 풀에서 묻는다(<see cref="Snapshot"/>).
///
/// 창 밖에서 가린 창의 모습을 그리는 것들도 가린다: Alt+Tab·작업 보기·작업 표시줄 미리보기(가릴 창이 있는 동안),
/// 가린 창의 최소화·복원 애니메이션(그 순간 프레임 전체).
/// </summary>
internal static class PrivateWindows
{
    private enum Kind { Pending, Private, Normal, Other }

    private sealed class Entry
    {
        public Kind Kind = Kind.Pending;
        public long LastTry;
        public bool Asking;
        /// <summary>브라우저 창 모양이 없다는 답을 처음 받은 시각. 0 이면 아직.</summary>
        public long FirstOther;
    }

    private static readonly object Lock = new();
    /// <summary>창 → 프로세스 이름. 창은 프로세스를 바꾸지 않으므로 pid 가 재사용돼도 틀리지 않는다.</summary>
    private static readonly Dictionary<IntPtr, string> Procs = new();
    /// <summary>주인이 없고 제목이 있는 Chrome 창(= 브라우저 창 후보). 최소화·숨김 중에도 들고 있다.</summary>
    private static readonly Dictionary<IntPtr, Entry> Chrome = new();
    /// <summary>가린 창의 직전 최소화 상태. 바뀌는 순간 애니메이션이 창 밖에 그려진다.</summary>
    private static readonly Dictionary<IntPtr, bool> Iconic = new();
    private static long _blackoutUntil;
    private static bool _anyBrave;

    /// <summary>브라우저 창 모양이 없다는 답이 이만큼 이어져야 브라우저 창이 아닌 것(PWA·화면 속 화면)으로 친다.</summary>
    private const int OtherAfterMs = 3000;
    private const int RetryMs = 300;
    private const int OtherRetryMs = 5000;
    /// <summary>가린 창이 최소화·복원될 때 프레임 전체를 검게 하는 시간. 애니메이션은 0.3초 안팎이다.</summary>
    private const int AnimationBlackoutMs = 700;

    private static readonly Regex IncognitoName = new(@" - (?:Google )?Chrome \([^()]*\)$", RegexOptions.CultureInvariant);
    private static readonly Regex NormalName = new(@" - (?:Google )?Chrome( - .+)?$", RegexOptions.CultureInvariant);

    private static readonly Condition RootViewCond =
        new PropertyCondition(AutomationElement.ClassNameProperty, "BrowserRootView");

    /// <summary>창 축소 화면을 그리는 셸 창. 가릴 창이 하나라도 있으면 영상에서 가린다.</summary>
    private static readonly HashSet<string> ThumbnailClasses = new(StringComparer.Ordinal)
    {
        "XamlExplorerHostIslandWindow",   // Win11 Alt+Tab·작업 보기·스냅 도우미
        "MultitaskingViewFrame",          // Win10 작업 보기
        "TaskListThumbnailWnd",           // 작업 표시줄 미리보기
    };

    /// <summary>창 제목을 글자로 내놓는 셸 창. 가릴 창이 있으면 화면 글자로 읽지 않는다.</summary>
    private static readonly HashSet<string> TitleListClasses = new(ThumbnailClasses, StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>한 번 훑은 결과. 위에서 아래로(Z 순서) 보이는 창과 가릴지 여부.</summary>
    public readonly record struct Win(IntPtr Handle, Rectangle Rect, bool Masked, bool Opaque);

    /// <summary>
    /// 지금 화면에 그려진 최상위 창을 Z 순서대로. 녹화 스레드에서 부른다 — UI 자동화를 기다리지 않는다.
    /// 가린 창이 최소화·복원되는 중이면 맨 앞에 화면 전체를 덮는 가림 창을 하나 넣는다.
    /// </summary>
    public static List<Win> Snapshot()
    {
        var hs = new List<IntPtr>(256);
        EnumWindows((h, _) => { hs.Add(h); return true; }, IntPtr.Zero);
        var now = Environment.TickCount64;
        var visible = hs.Where(IsWindowVisible).ToList();

        lock (Lock)
        {
            Forget(new HashSet<IntPtr>(hs));

            // 1) 창 목록을 먼저 채운다. 팝업이 주인보다 Z 순서가 앞이라, 한 번에 돌면 새 창의 팝업이 첫 장에서 샌다.
            var anyBrave = false;
            foreach (var h in visible)
            {
                var proc = ProcOf(h);
                if (proc == "brave") anyBrave = true;
                else if (proc == "chrome" && IsMain(h)) AskLater(EntryFor(h, now), h, now);
            }
            _anyBrave = anyBrave;
            var secret = AnySecret(IntPtr.Zero);

            // 2) 가린 창의 최소화·복원을 본다.
            foreach (var h in visible)
            {
                if (!MaskedLocked(h, secret)) continue;
                var ic = IsIconic(h);
                if (Iconic.TryGetValue(h, out var was) && was != ic) _blackoutUntil = now + AnimationBlackoutMs;
                Iconic[h] = ic;
            }

            // 3) 그려진 창만 사각형으로.
            var result = new List<Win>(visible.Count + 1);
            if (now < _blackoutUntil)
                result.Add(new Win(IntPtr.Zero, Rectangle.FromLTRB(-65536, -65536, 65536, 65536), true, false));
            foreach (var h in visible)
            {
                if (IsIconic(h) || Cloaked(h)) continue;
                if (!GetWindowRect(h, out var wr)) continue;
                var outer = Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom);
                if (outer.Width <= 0 || outer.Height <= 0) continue;

                var masked = MaskedLocked(h, secret) || (secret && ThumbnailClasses.Contains(ClassOf(h)));
                if (masked)
                {
                    // 가리는 쪽은 넉넉하게. 테두리·그림자까지 칠한다.
                    result.Add(new Win(h, outer, true, false));
                    continue;
                }
                var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                // 속이 비칠 수 있는 창(반투명·클릭 통과·DirectComposition 오버레이)은 뒤를 덮는다고 보지 않는다.
                // 덮는다고 봤다가 틀리면 새어 나간다.
                var opaque = (ex & (WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOREDIRECTIONBITMAP)) == 0;
                var rect = outer;
                if (opaque && DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT fr, Marshal.SizeOf<RECT>()) == 0)
                    // 덮는 쪽은 보이는 테두리까지만, 거기서 둥근 모서리만큼 더 안쪽으로. GetWindowRect 는 보이지 않는 크기 조절 테두리를 포함한다.
                    rect = Rectangle.FromLTRB(fr.Left, fr.Top, fr.Right, fr.Bottom);
                if (opaque) rect.Inflate(-CornerInset, -CornerInset);
                result.Add(new Win(h, rect, false, opaque && rect.Width > 0 && rect.Height > 0));
            }
            return result;
        }
    }

    /// <summary>
    /// 이 창의 내용을 기록에서 빼야 하는가. 앞 창 기록·화면 글자가 자기 타이머에서 부른다 —
    /// 여기서는 UI 자동화를 기다려도 되므로, 아직 모르는 Chrome 창이면 그 자리에서 묻는다.
    /// 가릴 창이 있는 동안의 작업 표시줄·Alt+Tab 도 참이다 (가린 창의 제목이 글자로 나온다).
    /// </summary>
    public static bool IsPrivate(IntPtr h)
    {
        var now = Environment.TickCount64;
        Entry? ask = null;
        lock (Lock)
        {
            var proc = ProcOf(h);
            if (proc == "chrome" && IsMain(h))
            {
                var e = EntryFor(h, now);
                var wait = e.Kind == Kind.Pending ? RetryMs : OtherRetryMs;
                if (e.Kind is Kind.Pending or Kind.Other && !e.Asking && now - e.LastTry >= wait)
                {
                    e.Asking = true;
                    e.LastTry = now;
                    ask = e;
                }
            }
        }
        // 창 하나를 묻는 데 수~수백 ms. 잠금을 쥔 채 묻지 않는다.
        if (ask != null)
        {
            var k = Ask(h);
            lock (Lock) Settle(ask, k, Environment.TickCount64);
        }
        lock (Lock)
        {
            var secret = AnySecret(IntPtr.Zero);
            return MaskedLocked(h, secret) || (secret && TitleListClasses.Contains(ClassOf(h)));
        }
    }

    /// <summary>Chrome 시크릿 창(또는 아직 모르는 창)이 하나라도 있는가. 최소화된 것도 센다. Chrome 미디어 제목을 뺄지 정할 때 쓴다.</summary>
    public static bool AnyChromePrivate()
    {
        lock (Lock) return Chrome.Values.Any(e => e.Kind is Kind.Private or Kind.Pending);
    }

    // ── 내부 (잠금 안에서 부른다) ────────────────────────────────────────────

    private static bool MaskedLocked(IntPtr h, bool secret)
    {
        var proc = ProcOf(h);
        if (proc == "brave") return true;
        if (proc != "chrome") return false;

        // 메뉴·주소창 드롭다운·탭 미리보기는 따로 뜬 최상위 창이다. 주인 창을 따른다.
        var owner = GetAncestor(h, GA_ROOTOWNER);
        if (owner != IntPtr.Zero && owner != h)
            return Chrome.TryGetValue(owner, out var oe) ? Masks(oe, owner) : secret;

        // 주인도 제목도 없는 창(알림·떠 있는 팝업). 가릴 창이 있으면 가린다.
        if (GetWindowTextLength(h) == 0) return secret;
        return Chrome.TryGetValue(h, out var e) ? Masks(e, h) : true;
    }

    private static bool Masks(Entry e, IntPtr h) => e.Kind switch
    {
        Kind.Normal => false,
        Kind.Other => AnySecret(h),
        _ => true,
    };

    private static bool AnySecret(IntPtr except) =>
        _anyBrave || Chrome.Any(kv => kv.Key != except && kv.Value.Kind is Kind.Private or Kind.Pending);

    private static bool IsMain(IntPtr h)
    {
        var owner = GetAncestor(h, GA_ROOTOWNER);
        return (owner == IntPtr.Zero || owner == h) && GetWindowTextLength(h) > 0;
    }

    private static Entry EntryFor(IntPtr h, long now)
    {
        if (!Chrome.TryGetValue(h, out var e))
        {
            e = new Entry { LastTry = now - RetryMs };
            Chrome[h] = e;
        }
        return e;
    }

    /// <summary>모르는 창이면 스레드 풀에서 묻는다. 일반·시크릿은 한 번 정해지면 다시 묻지 않는다.</summary>
    private static void AskLater(Entry e, IntPtr h, long now)
    {
        var wait = e.Kind switch { Kind.Pending => RetryMs, Kind.Other => OtherRetryMs, _ => -1 };
        if (wait < 0 || e.Asking || now - e.LastTry < wait) return;
        e.Asking = true;
        e.LastTry = now;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var k = Ask(h);
            lock (Lock) Settle(e, k, Environment.TickCount64);
        });
    }

    /// <summary>
    /// 답을 반영한다. 일반·시크릿은 바로. "브라우저 창 모양이 없다"는 답은 3초 넘게 이어질 때만 믿는다 —
    /// 막 뜬 창은 트리가 덜 만들어져 있다. 오류는 아무것도 바꾸지 않는다(계속 가린 채 다시 묻는다).
    /// </summary>
    private static void Settle(Entry e, Kind k, long now)
    {
        e.Asking = false;
        switch (k)
        {
            case Kind.Private or Kind.Normal:
                e.Kind = k;
                break;
            case Kind.Other:
                if (e.FirstOther == 0) e.FirstOther = now;
                if (now - e.FirstOther >= OtherAfterMs) e.Kind = Kind.Other;
                break;
        }
    }

    /// <summary>
    /// UI 자동화로 묻는다. 일반 창 모양이 확인될 때만 Normal. 시크릿 모양이거나 처음 보는 모양이면 Private.
    /// 트리에 브라우저 창 모양이 없으면 Other, 이름이 비었거나 오류면 Pending.
    /// </summary>
    private static Kind Ask(IntPtr h)
    {
        try
        {
            var root = AutomationElement.FromHandle(h);
            var view = root.FindFirst(TreeScope.Children, RootViewCond);
            if (view == null) return Kind.Other;
            var name = view.Current.Name;
            if (string.IsNullOrEmpty(name)) return Kind.Pending;
            if (IncognitoName.IsMatch(name)) return Kind.Private;
            return NormalName.IsMatch(name) ? Kind.Normal : Kind.Private;
        }
        catch (Exception)
        {
            return Kind.Pending;
        }
    }

    private static void Forget(HashSet<IntPtr> alive)
    {
        foreach (var k in Chrome.Keys.Where(k => !alive.Contains(k)).ToList()) Chrome.Remove(k);
        foreach (var k in Iconic.Keys.Where(k => !alive.Contains(k)).ToList()) Iconic.Remove(k);
        foreach (var k in Procs.Keys.Where(k => !alive.Contains(k)).ToList()) Procs.Remove(k);
    }

    private static string ProcOf(IntPtr h)
    {
        if (Procs.TryGetValue(h, out var n)) return n;
        GetWindowThreadProcessId(h, out var pid);
        try { using var p = Process.GetProcessById((int)pid); n = p.ProcessName.ToLowerInvariant(); }
        catch (Exception) { n = "?"; }
        if (Procs.Count > 4096) Procs.Clear();
        Procs[h] = n;
        return n;
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(128);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool Cloaked(IntPtr h) =>
        DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0;

    // ── Win32 ────────────────────────────────────────────────────────────────

    /// <summary>Win11 창의 둥근 모서리. 덮는 창을 이만큼 줄여 모서리 틈으로 뒤가 새지 않게 한다.</summary>
    private const int CornerInset = 8;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x80000;
    private const long WS_EX_TRANSPARENT = 0x20;
    private const long WS_EX_NOREDIRECTIONBITMAP = 0x200000;
    private const uint GA_ROOTOWNER = 3;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT v, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
}
