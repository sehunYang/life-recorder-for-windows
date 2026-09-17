using System.Windows.Automation;

namespace LifeRecorderWin.Capture;

/// <summary>
/// 브라우저 창에서 **주소창 URL 과 문서 스크롤 위치만** 읽는다. 페이지 내용은 읽지 않는다.
///
/// 창 제목만으로는 "무엇을 봤나"는 알아도 원문을 다시 찾을 수 없다. URL 이 있어야
/// 밤에 기사 본문·자막을 원문에서 가져올 수 있고, 스크롤 위치가 있어야 끝까지 읽었는지 안다.
///
/// UI 자동화 트리를 매번 뒤지면 큰 페이지에서 1초 가까이 걸린다. 그래서 창(hwnd)마다
/// 주소창 요소를, URL 마다 문서 요소를 한 번 찾아 두고 그 뒤로는 값만 읽는다.
/// 요소가 사라지면(탭 닫힘·창 닫힘) 예외가 나고, 그때 캐시를 비워 다음에 다시 찾는다.
///
/// 폰의 비밀번호 칸 규칙과 같은 선을 지킨다 — 비밀번호로 표시된 입력란은 읽지 않고,
/// URL 모양이 아닌 값(검색어를 치는 중)은 버린다.
/// </summary>
internal sealed class BrowserProbe
{
    private static readonly Condition OmniboxCond = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
        new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true),
        new PropertyCondition(AutomationElement.IsPasswordProperty, false));

    private static readonly Condition DocCond = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
        new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));

    private IntPtr _hwnd;
    private AutomationElement? _omnibox;
    private AutomationElement? _doc;
    private string? _docUrl;

    /// <summary>앞 창의 주소창 값. 못 읽거나 URL 모양이 아니면 null.</summary>
    public string? Url(IntPtr hwnd)
    {
        try
        {
            if (hwnd != _hwnd || _omnibox == null)
            {
                _hwnd = hwnd;
                _omnibox = null;
                _doc = null;
                _docUrl = null;
                var root = AutomationElement.FromHandle(hwnd);
                // 트리 순서상 도구 모음(주소창)이 문서보다 앞에 온다. 첫 번째 입력란이 주소창이다.
                _omnibox = root.FindFirst(TreeScope.Descendants, OmniboxCond);
                if (_omnibox == null) return null;
            }
            var v = ((ValuePattern)_omnibox.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
            return Normalize(v);
        }
        catch (Exception)
        {
            // ElementNotAvailable · COM 오류. 창이 닫혔거나 바뀌었다. 다음 틱에 다시 찾는다.
            _omnibox = null;
            _doc = null;
            return null;
        }
    }

    /// <summary>
    /// 지금 보고 있는 문서의 세로 스크롤 위치(0~1)와 한 화면이 문서에서 차지하는 비율(0~1).
    /// 스크롤할 것이 없는 짧은 문서면 null.
    /// </summary>
    public (double pos, double view)? Scroll(IntPtr hwnd, string url)
    {
        try
        {
            if (hwnd != _hwnd) return null;
            if (_doc == null || url != _docUrl)
            {
                _doc = AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants, DocCond);
                _docUrl = url;
                if (_doc == null) return null;
            }
            var sp = ((ScrollPattern)_doc.GetCurrentPattern(ScrollPattern.Pattern)).Current;
            if (!sp.VerticallyScrollable || sp.VerticalScrollPercent < 0) return null;
            return (sp.VerticalScrollPercent / 100.0, sp.VerticalViewSize / 100.0);
        }
        catch (Exception)
        {
            _doc = null;
            return null;
        }
    }

    /// <summary>
    /// 주소창은 초점이 있을 때는 <c>https://</c> 를 붙여 주고 없을 때는 뗀다. 같은 페이지가
    /// 두 값으로 보이지 않게 스킴을 뗀다. 공백이 있으면 URL 이 아니라 치는 중인 검색어다.
    /// </summary>
    private static string? Normalize(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v.Trim();
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s[8..];
        else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s[7..];
        if (s.Contains(' ') || !s.Contains('.')) return null;
        return s.Length > Config.AppUrlMaxLength ? s[..Config.AppUrlMaxLength] : s;
    }
}
