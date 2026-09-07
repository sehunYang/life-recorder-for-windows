using System.Drawing;
using LifeRecorderWin.Capture;

namespace LifeRecorderWin.Ui;

/// <summary>
/// 창 안의 좌표와 크기를 화면 배율에 맞춰 늘린다.
///
/// 이 앱은 캡처 때문에 PerMonitorV2 로 DPI 인식을 켜 두는데, 그러면 WinForms 가 **글꼴만**
/// 배율에 맞춰 키우고 코드로 박아 넣은 <c>Location</c>/<c>Size</c> 는 그대로 둔다.
/// (디자이너가 만든 폼이라면 자동으로 같이 늘어나지만 이 창들은 코드로 배치한다)
/// 그래서 150% 화면에서 글자가 칸을 넘쳐 잘렸다. 좌표를 여기서 같이 늘린다.
///
/// 기준은 96dpi(100%)에서 보기 좋은 값이다.
/// </summary>
internal static class Px
{
    private static readonly float Factor = Dpi.SystemDpi() / 96f;

    public static Point P(int x, int y) => new((int)(x * Factor), (int)(y * Factor));

    public static Size Z(int w, int h) => new((int)(w * Factor), (int)(h * Factor));
}
