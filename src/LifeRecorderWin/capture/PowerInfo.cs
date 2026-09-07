using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LifeRecorderWin.Capture;

/// <summary>
/// 배터리로 돌고 있는지, 데이터 요금이 붙는 회선인지 묻는다.
///
/// 안드로이드 앱에는 "Wi-Fi 전용 / 충전 중에만 업로드" 제약이 있었다.
/// 데스크톱으로 옮기면서 뜻이 없다고 보고 뺐는데, **노트북에 깔면 그대로 되살아난다.**
/// 수업용 노트북을 배터리로 쓰거나 휴대폰 핫스팟에 물려 있을 때
/// 시간당 200MB 넘는 영상을 그대로 올려 버리면 곤란하다.
///
/// 데스크톱에서는 전부 조용한 no-op 이다 — 배터리가 없고, 유선은 종량제로 잡히지 않는다.
/// </summary>
internal static class PowerInfo
{
    /// <summary>배터리가 달린 기기인가. 데스크톱이면 false.</summary>
    public static bool HasBattery =>
        (SystemInformation.PowerStatus.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0;

    /// <summary>전원이 빠진 채 배터리로 돌고 있는가.</summary>
    public static bool OnBattery =>
        HasBattery && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;

    /// <summary>배터리 잔량(%). 배터리가 없거나 알 수 없으면 null.</summary>
    public static int? BatteryPercent
    {
        get
        {
            if (!HasBattery) return null;
            // BatteryLifePercent 는 0~1 이고, 알 수 없으면 255 가 온다.
            var p = SystemInformation.PowerStatus.BatteryLifePercent;
            if (p is < 0f or > 1f) return null;
            return (int)Math.Round(p * 100);
        }
    }

    /// <summary>
    /// 데이터 요금이 붙는 회선인가 (휴대폰 핫스팟, LTE 동글, 사용자가 "종량제 연결"로 표시한 Wi-Fi).
    ///
    /// 알 수 없으면 **false 로 본다.** 못 올리는 쪽보다 잘못 올리는 쪽이 낫다 —
    /// 데이터를 Drive 로 보내는 게 이 앱의 목적이고, 판단이 안 서서 영영 안 올리면 그게 더 나쁘다.
    /// </summary>
    public static bool IsMetered()
    {
        object? com = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidNetworkListManager);
            if (type == null) return false;
            com = Activator.CreateInstance(type);
            if (com is not INetworkCostManager mgr) return false;

            mgr.GetCost(out var cost, IntPtr.Zero);
            // UNKNOWN(0) 과 UNRESTRICTED(1) 만 마음 놓고 올려도 되는 회선이다.
            const uint unrestricted = 0x1;
            const uint unknown = 0x0;
            return cost != unknown && cost != unrestricted;
        }
        catch (Exception)
        {
            // COM 이 없거나 막힌 환경. 판단을 포기하고 올린다.
            return false;
        }
        finally
        {
            if (com != null) Marshal.ReleaseComObject(com);
        }
    }

    /// <summary>사람이 읽는 상태 한 줄. 로그와 상태창에 쓴다.</summary>
    public static string Describe()
    {
        var parts = new List<string>();
        if (HasBattery)
        {
            var pct = BatteryPercent;
            parts.Add(OnBattery
                ? "배터리" + (pct != null ? $" {pct}%" : "")
                : "전원 연결됨");
        }
        if (IsMetered()) parts.Add("종량제 회선");
        return parts.Count > 0 ? string.Join(" · ", parts) : "";
    }

    // ── Win32 (netprofm) ─────────────────────────────────────────────────────
    //
    // WinRT 의 NetworkInformation 쪽이 쓰기는 편하지만 대상 프레임워크를
    // net8.0-windows10.0.19041.0 으로 올려야 한다. 이 COM 인터페이스는 Windows 8 부터 있고
    // 프레임워크를 건드리지 않아도 된다.

    private static readonly Guid ClsidNetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    [ComImport]
    [Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        // vtable 의 첫 번째 메서드. 뒤의 것들은 쓰지 않으므로 선언하지 않는다.
        void GetCost(out uint cost, IntPtr destIpAddr);
    }
}
