using Microsoft.Win32;

namespace LifeRecorderWin;

/// <summary>
/// Windows 로그인 시 자동 시작. HKCU 라 관리자 권한이 필요 없다.
///
/// 안드로이드는 <c>BootReceiver</c> 가 부팅 후 서비스를 되살렸는데, 여기서 그 자리를 맡는다.
/// 켜 두면 로그인할 때마다 앱이 뜨고, 앱은 저장된 ON/OFF 상태를 그대로 이어받는다.
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LifeRecorder";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception e)
        {
            Log.Warn("자동 시작 상태 확인 실패: " + e.Message);
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe == null)
                {
                    Log.Warn("실행 파일 경로를 몰라 자동 시작을 켜지 못했습니다");
                    return;
                }
                key.SetValue(ValueName, "\"" + exe + "\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            Log.Info("자동 시작 " + (enabled ? "켬" : "끔"));
        }
        catch (Exception e)
        {
            Log.Warn("자동 시작 설정 실패: " + e.Message);
        }
    }
}
