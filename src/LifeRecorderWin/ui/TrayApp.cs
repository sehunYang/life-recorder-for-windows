using System.Windows.Forms;
using LifeRecorderWin.Capture;
using LifeRecorderWin.Upload;

namespace LifeRecorderWin.Ui;

/// <summary>
/// 앱의 수명을 쥐고 있는 트레이 상주 껍데기.
/// 창을 닫아도 여기가 살아 있어서 녹화가 이어진다. 안드로이드의 포그라운드 서비스 알림에 해당한다.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly DriveAuth _auth = new();
    private readonly UploadScheduler _uploads;
    private readonly RecordingService _recording;
    private readonly ToolStripMenuItem _toggleItem;
    private MainForm? _form;

    /// <summary>연결이 끊긴 순간에만 한 번 알리기 위해 직전 값을 들고 있는다.</summary>
    private bool _driveLinked;

    public TrayApp(bool startHidden)
    {
        _uploads = new UploadScheduler(_auth);
        _recording = new RecordingService(_uploads);

        _toggleItem = new ToolStripMenuItem("기록 시작 (ON)", null, (_, _) =>
            _recording.SetEnabled(!Prefs.Current.RecordingEnabled));

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("상태 보기", null, (_, _) => ShowForm()));
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("지금 업로드", null, (_, _) => _uploads.RequestNow()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("로그 폴더 열기", null, (_, _) => LinkDialog.OpenUrl(Storage.LogDir)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("끝내기", null, (_, _) => Quit()));

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.For(RecorderState.Current),
            Text = "Life Recorder",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowForm();

        RecorderState.Changed += OnStateChanged;

        // 설정에 적힌 자동 시작 값을 실제 레지스트리와 맞춘다 (사용자가 밖에서 지웠을 수 있다).
        if (Prefs.Current.AutoStart != AutoStart.IsEnabled()) AutoStart.Set(Prefs.Current.AutoStart);

        // 지난 번 마지막 업로드 시각을 되살린다. 안 그러면 재시작 직후 "업로드 없음"으로 보인다.
        _driveLinked = _auth.IsLinked;
        RecorderState.Update(s => s with
        {
            DriveLinked = _driveLinked,
            LastUploadAt = Prefs.Current.LastUploadAtUtc?.ToLocalTime(),
        });

        _uploads.Start();
        _recording.Restore();
        OnStateChanged(RecorderState.Current);

        if (!startHidden) ShowForm();
    }

    private void ShowForm()
    {
        if (_form == null || _form.IsDisposed)
            _form = new MainForm(_recording, _uploads, _auth);
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private void OnStateChanged(Status s)
    {
        void Apply()
        {
            _tray.Icon = TrayIcons.For(s);
            _toggleItem.Text = s.RecordingEnabled ? "기록 중지 (OFF)" : "기록 시작 (ON)";

            var line = !s.RecordingEnabled ? "꺼짐"
                : s.ScreenPausedReason != null ? "쉬는 중 — " + s.ScreenPausedReason
                : s.ScreenRecording ? "기록 중"
                : "다시 시작하는 중";
            var pending = s.PendingFiles > 0 ? $"\n대기 {s.PendingFiles}개 · {Storage.FmtBytes(s.PendingBytes)}" : "";
            // NotifyIcon.Text 는 63자 제한이 있다.
            var text = "Life Recorder — " + line + pending;
            _tray.Text = text.Length > 62 ? text[..62] : text;

            // 연결이 끊어진 순간에만 한 번 알린다. 안드로이드의 "Google 계정 연결 필요" 알림에 해당한다.
            // 녹화는 계속되고 파일은 로컬에 쌓이므로, 알아채지 못하면 디스크만 찬다.
            if (_driveLinked && !s.DriveLinked)
            {
                _tray.ShowBalloonTip(10_000, "Life Recorder",
                    "Google 계정 연결이 필요합니다. 녹화는 계속되고 파일은 로컬에 쌓입니다.",
                    ToolTipIcon.Warning);
            }
            _driveLinked = s.DriveLinked;
        }

        if (_tray.ContextMenuStrip is { IsHandleCreated: true } menu && menu.InvokeRequired) menu.BeginInvoke(Apply);
        else Apply();
    }

    private void Quit()
    {
        RecorderState.Changed -= OnStateChanged;
        _tray.Visible = false;
        _recording.Dispose();
        _uploads.Dispose();
        _tray.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tray.Dispose();
        base.Dispose(disposing);
    }
}
