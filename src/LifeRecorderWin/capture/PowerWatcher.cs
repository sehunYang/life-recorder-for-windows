using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LifeRecorderWin.Capture;

/// <summary>
/// 언제 화면을 담을 게 없는지 알려 준다.
///
/// 안드로이드는 <c>ACTION_SCREEN_OFF</c> / <c>ACTION_USER_PRESENT</c> 를 받아
/// 가상 디스플레이에서 서피스를 떼는 식으로 캡처만 껐다. 여기서 그 자리를 맡는다.
///
///  - 세션 잠금: 잠금화면은 다른 데스크톱이라 gdigrab 이 담을 수 있는 게 없다
///  - 모니터 꺼짐: 담을 화면 자체가 없다
///  - 절전 진입/복귀
///  - 모니터 구성 변경: 프레임 크기가 달라지므로 세션을 다시 잡아야 한다
/// </summary>
internal sealed class PowerWatcher : IDisposable
{
    /// <summary>멈춰야 할 이유. null 이면 다시 담아도 된다.</summary>
    public event Action<string?>? PauseReasonChanged;

    /// <summary>모니터가 붙거나 빠져 프레임 크기가 달라졌다.</summary>
    public event Action? DisplayLayoutChanged;

    private readonly MessageWindow _window;
    private IntPtr _displayNotify;

    private bool _locked;
    private bool _displayOff;
    private bool _suspended;

    /// <summary>같은 이유를 두 번 알리지 않기 위해 직전에 알린 값을 들고 있는다.</summary>
    private string? _lastReason;

    /// <summary>배터리 잔량은 이벤트로 오지 않는다. 주기적으로 들여다본다.</summary>
    private readonly System.Threading.Timer _poll;

    public PowerWatcher()
    {
        _window = new MessageWindow(OnDisplayState);
        _displayNotify = RegisterPowerSettingNotification(
            _window.Handle, ref GuidConsoleDisplayState, DeviceNotifyWindowHandle);
        if (_displayNotify == IntPtr.Zero)
            Log.Warn("모니터 전원 알림 등록 실패 — 화면이 꺼져도 계속 녹화합니다");

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerMode;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettings;

        _lastReason = PauseReason;
        _poll = new System.Threading.Timer(
            _ => Publish(), null, Config.PowerPollInterval, Config.PowerPollInterval);
    }

    /// <summary>지금 멈춰야 하는 이유. null 이면 담아도 된다.</summary>
    public string? PauseReason =>
        _suspended ? "절전 중" :
        _locked ? "잠금 상태" :
        _displayOff ? "모니터 꺼짐" :
        BatteryTooLow();

    /// <summary>
    /// 배터리가 얼마 안 남았으면 스스로 멈춘다. 갑자기 꺼지면 쓰던 세그먼트가 통째로 날아간다
    /// (mp4 는 닫힐 때 moov 가 쓰인다). 남은 전력이 있을 때 정상적으로 닫는 편이 낫다.
    /// 배터리가 없는 컴퓨터에서는 <see cref="PowerInfo.OnBattery"/> 가 항상 false 라 걸리지 않는다.
    /// </summary>
    private static string? BatteryTooLow()
    {
        if (!Prefs.Current.SaveOnBattery || !PowerInfo.OnBattery) return null;
        var pct = PowerInfo.BatteryPercent;
        return pct != null && pct < Config.BatteryStopPercent
            ? $"배터리 {pct}% — {Config.BatteryStopPercent}% 아래라 멈춤"
            : null;
    }

    /// <summary>이유가 실제로 바뀌었을 때만 알린다. 주기 확인이 매분 두드리지 않게.</summary>
    private void Publish()
    {
        var reason = PauseReason;
        if (reason == _lastReason) return;
        _lastReason = reason;
        PauseReasonChanged?.Invoke(reason);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _locked = true;
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
            case SessionSwitchReason.SessionLogon:
                _locked = false;
                break;
            default:
                return;
        }
        Log.Info("세션 상태: " + e.Reason);
        Publish();
    }

    private void OnPowerMode(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                _suspended = true;
                break;
            case PowerModes.Resume:
                _suspended = false;
                break;
            case PowerModes.StatusChange:
                // 전원 어댑터를 꽂거나 뽑았다. 배터리 판단이 달라진다.
                break;
            default:
                return;
        }
        Log.Info("전원 상태: " + e.Mode + " — " + PowerInfo.Describe());
        Publish();
    }

    private void OnDisplaySettings(object? sender, EventArgs e)
    {
        Log.Info("모니터 구성이 바뀌었습니다");
        DisplayLayoutChanged?.Invoke();
    }

    /// <summary>0=꺼짐, 1=켜짐, 2=흐려짐(켜진 것으로 본다).</summary>
    private void OnDisplayState(int state)
    {
        var off = state == 0;
        if (off == _displayOff) return;
        _displayOff = off;
        Log.Info("모니터 전원: " + (off ? "꺼짐" : "켜짐"));
        Publish();
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerMode;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettings;
        _poll.Dispose();
        if (_displayNotify != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_displayNotify);
            _displayNotify = IntPtr.Zero;
        }
        _window.DestroyHandle();
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8021;
    private const int DeviceNotifyWindowHandle = 0x00000000;

    private static Guid GuidConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    /// <summary>WM_POWERBROADCAST 를 받기 위한 보이지 않는 창.</summary>
    private sealed class MessageWindow : NativeWindow
    {
        private readonly Action<int> _onDisplayState;

        public MessageWindow(Action<int> onDisplayState)
        {
            _onDisplayState = onDisplayState;
            CreateHandle(new CreateParams { Caption = "LifeRecorderPowerWatcher" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmPowerBroadcast && (int)m.WParam == PbtPowerSettingChange && m.LParam != IntPtr.Zero)
            {
                var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(m.LParam);
                if (setting.PowerSetting == GuidConsoleDisplayState) _onDisplayState(setting.Data);
            }
            base.WndProc(ref m);
        }
    }
}
