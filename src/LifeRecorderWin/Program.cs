using System.Windows.Forms;
using LifeRecorderWin.Ui;

namespace LifeRecorderWin;

internal static class Program
{
    /// <summary>
    /// 두 번 뜨면 같은 화면을 두 벌 담고 같은 파일을 두 번 올리게 된다. 한 번만 뜬다.
    /// </summary>
    private const string MutexName = @"Local\LifeRecorder.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Life Recorder 가 이미 실행 중입니다. 작업 표시줄 오른쪽 트레이를 확인하세요.",
                "Life Recorder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 자동 시작으로 뜬 경우에는 창을 띄우지 않고 트레이에만 앉는다.
        var startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase))
                          || AutoStart.IsEnabled();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("처리되지 않은 예외: " + e.ExceptionObject);
        Application.ThreadException += (_, e) =>
            Log.Error("UI 스레드 예외: " + e.Exception);

        Log.Info("=== Life Recorder for Windows 시작 ===");
        // 컴퓨터마다 달라지는 것들을 한 줄로 남긴다. 세 대에 깔아 두면 로그만 보고 어느 것인지 안다.
        Log.Info(Environment());
        try
        {
            Application.Run(new TrayApp(startHidden));
        }
        finally
        {
            Log.Info("=== 종료 ===");
        }
    }

    /// <summary>이 컴퓨터가 어떤 환경인지 한 줄. 세 대에 깔면 로그가 서로 헷갈린다.</summary>
    private static string Environment()
    {
        var name = Storage.HasDeviceName ? Storage.DeviceName : "(이름 없음)";
        var battery = Capture.PowerInfo.HasBattery
            ? (Capture.PowerInfo.OnBattery ? "배터리" : "전원 연결됨")
            : "배터리 없음";
        var line = Capture.PowerInfo.IsMetered() ? "종량제" : "일반";
        return $"환경: [{name}] 화면 배율 {Capture.Dpi.SystemScalePercent()} · {battery} · 회선 {line}";
    }
}
