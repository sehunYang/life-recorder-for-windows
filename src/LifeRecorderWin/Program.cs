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
        try
        {
            Application.Run(new TrayApp(startHidden));
        }
        finally
        {
            Log.Info("=== 종료 ===");
        }
    }
}
