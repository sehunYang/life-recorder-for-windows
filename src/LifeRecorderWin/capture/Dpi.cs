using System.Runtime.InteropServices;

namespace LifeRecorderWin.Capture;

/// <summary>
/// 화면 배율을 읽어 실제 축소 배율을 정한다.
///
/// 앱은 PerMonitorV2 로 DPI 인식이 켜져 있어서 캡처 입력이 언제나 **물리 픽셀**이다.
/// 그 크기는 같은 모니터라도 Windows 배율 설정(100%/125%/150%…)에 따라 달라지므로,
/// 물리 픽셀 대비 배율을 상수로 박아 두면 배율이 다른 컴퓨터에서 결과가 달라진다.
///
/// 판독을 좌우하는 것은 **글자 하나가 결과 영상에서 몇 픽셀을 차지하느냐**이고
/// 그건 논리 해상도에 비례한다. 그래서 목표를 논리 해상도 기준으로 두고
/// 물리 대비 배율은 여기서 역산한다.
///
///   150% 화면: 0.75 × 96/144 = 0.5   (집 컴퓨터에서 실측으로 고른 값과 같다)
///   100% 화면: 0.75 × 96/96  = 0.75
/// </summary>
internal static class Dpi
{
    private const double BaseDpi = 96.0;

    /// <summary>물리 픽셀에 곱할 축소 배율. 1.0 이면 줄이지 않는다.</summary>
    public static double EffectiveScale
    {
        get
        {
            if (Config.ScreenScaleOverride > 0) return Config.ScreenScaleOverride;
            var scale = Config.ScreenTargetLogicalScale * BaseDpi / SystemDpi();
            // 원본보다 키우지는 않는다. 없는 정보가 생기지 않고 용량만 는다.
            return Math.Clamp(scale, 0.1, 1.0);
        }
    }

    /// <summary>주 모니터의 DPI. 모니터마다 배율이 다르면 주 모니터 기준이 된다.</summary>
    public static int SystemDpi()
    {
        try
        {
            var dpi = GetDpiForSystem();
            return dpi >= 48 ? (int)dpi : (int)BaseDpi;
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 10 1607 미만. 배율을 모르니 100% 로 본다.
            return (int)BaseDpi;
        }
        catch (DllNotFoundException)
        {
            return (int)BaseDpi;
        }
    }

    /// <summary>사람이 읽는 배율 표시. "150%" 꼴.</summary>
    public static string SystemScalePercent() => $"{(int)Math.Round(SystemDpi() / BaseDpi * 100)}%";

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
