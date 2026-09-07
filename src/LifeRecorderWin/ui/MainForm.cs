using System.Drawing;
using System.Windows.Forms;
using LifeRecorderWin.Capture;
using LifeRecorderWin.Upload;

namespace LifeRecorderWin.Ui;

/// <summary>
/// 상태를 보여 주고 ON/OFF 를 누르는 창. 안드로이드 <c>MainScreen.kt</c> 의 자리다.
/// 닫아도 앱은 트레이에 남는다. 끝내려면 트레이 메뉴에서 "끝내기".
/// </summary>
internal sealed class MainForm : Form
{
    private readonly RecordingService _recording;
    private readonly UploadScheduler _uploads;
    private readonly DriveAuth _auth;

    private readonly Label _state = new();
    private readonly Label _screen = new();
    private readonly Label _pending = new();
    private readonly Label _drive = new();
    private readonly Label _error = new();
    private readonly Button _toggle = new();
    private readonly Button _upload = new();
    private readonly Button _link = new();
    private readonly CheckBox _autoStart = new();
    private readonly System.Windows.Forms.Timer _tick = new();

    public MainForm(RecordingService recording, UploadScheduler uploads, DriveAuth auth)
    {
        _recording = recording;
        _uploads = uploads;
        _auth = auth;

        Text = "Life Recorder";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 396);
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = true;

        BuildLayout();

        RecorderState.Changed += OnStateChanged;
        _tick.Interval = 1000;
        _tick.Tick += (_, _) => Render(RecorderState.Current);
        _tick.Start();

        Render(RecorderState.Current);
    }

    private void BuildLayout()
    {
        _state.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
        _state.Location = new Point(20, 18);
        _state.Size = new Size(420, 32);

        _screen.Location = new Point(20, 54);
        _screen.Size = new Size(420, 20);
        _screen.ForeColor = Color.FromArgb(90, 90, 90);

        var sep1 = new Label { BorderStyle = BorderStyle.Fixed3D, Location = new Point(20, 86), Size = new Size(420, 2) };

        _pending.Location = new Point(20, 100);
        _pending.Size = new Size(420, 20);

        _drive.Location = new Point(20, 124);
        _drive.Size = new Size(420, 20);

        _error.Location = new Point(20, 148);
        _error.Size = new Size(420, 36);
        _error.ForeColor = Color.FromArgb(180, 40, 40);

        _toggle.Location = new Point(20, 196);
        _toggle.Size = new Size(200, 44);
        _toggle.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _toggle.Click += (_, _) => _recording.SetEnabled(!Prefs.Current.RecordingEnabled);

        _upload.Text = "지금 업로드";
        _upload.Location = new Point(240, 196);
        _upload.Size = new Size(200, 44);
        _upload.Click += (_, _) =>
        {
            _uploads.RequestNow();
            RecorderState.Update(s => s with { LastUploadError = null });
        };

        _link.Location = new Point(20, 252);
        _link.Size = new Size(200, 34);
        _link.Click += async (_, _) => await ToggleLinkAsync();

        var openFolder = new Button
        {
            Text = "로컬 폴더 열기",
            Location = new Point(240, 252),
            Size = new Size(200, 34),
        };
        openFolder.Click += (_, _) => LinkDialog.OpenUrl(Storage.BaseDir);

        _autoStart.Text = "Windows 시작할 때 자동으로 실행";
        _autoStart.Location = new Point(20, 300);
        _autoStart.Size = new Size(420, 24);
        _autoStart.Checked = AutoStart.IsEnabled();
        _autoStart.CheckedChanged += (_, _) =>
        {
            AutoStart.Set(_autoStart.Checked);
            Prefs.Update(p => p.AutoStart = _autoStart.Checked);
        };

        var note = new Label
        {
            Text = "잠금·모니터 꺼짐·절전 동안에는 담을 화면이 없어 쉬었다가, 풀리면 바로 다시 시작합니다.\n"
                   + "업로드가 끝난 파일은 로컬에서 지웁니다. 창을 닫아도 트레이에 남습니다.",
            Location = new Point(20, 330),
            Size = new Size(420, 50),
            ForeColor = Color.FromArgb(110, 110, 110),
        };

        Controls.AddRange(new Control[]
        {
            _state, _screen, sep1, _pending, _drive, _error,
            _toggle, _upload, _link, openFolder, _autoStart, note,
        });
    }

    private async Task ToggleLinkAsync()
    {
        if (_auth.IsLinked)
        {
            var confirm = MessageBox.Show(this,
                "Google 계정 연결을 해제하면 업로드가 멈춥니다. 녹화는 계속되고 파일은 로컬에 쌓입니다.\n계속할까요?",
                "Life Recorder", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (confirm != DialogResult.OK) return;
            DriveAuth.Unlink();
            RecorderState.Update(s => s with { DriveLinked = false, LastUploadError = null });
            Render(RecorderState.Current);
            return;
        }

        var cred = Credentials.Load();
        using var dialog = new LinkDialog(cred.ClientId, cred.ClientSecret);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _link.Enabled = false;
        _link.Text = "브라우저에서 동의 중…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            await _auth.LinkAsync(dialog.ClientId, dialog.ClientSecret, cts.Token);
            RecorderState.Update(s => s with { DriveLinked = true, LastUploadError = null });
            _uploads.RequestNow();
            MessageBox.Show(this, "연결되었습니다.", "Life Recorder");
        }
        catch (Exception e)
        {
            Log.Error("계정 연결 실패: " + e.Message);
            MessageBox.Show(this, "연결하지 못했습니다.\n\n" + e.Message, "Life Recorder",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _link.Enabled = true;
            Render(RecorderState.Current);
        }
    }

    private void OnStateChanged(Status s)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(() => Render(s));
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Render(Status s)
    {
        if (IsDisposed) return;

        if (!s.RecordingEnabled)
        {
            _state.Text = "꺼짐";
            _state.ForeColor = Color.FromArgb(110, 110, 110);
        }
        else if (s.ScreenPausedReason != null)
        {
            _state.Text = "쉬는 중 — " + s.ScreenPausedReason;
            _state.ForeColor = Color.FromArgb(190, 130, 20);
        }
        else if (s.ScreenRecording)
        {
            _state.Text = "기록 중";
            _state.ForeColor = Color.FromArgb(190, 45, 45);
        }
        else
        {
            _state.Text = "다시 시작하는 중";
            _state.ForeColor = Color.FromArgb(190, 130, 20);
        }

        _screen.Text = s.ScreenRecording && s.CaptureSize != null
            ? $"{s.CaptureSize} · {Config.ScreenFps}fps · 이번 세그먼트 "
              + (s.CurrentSegmentStart?.ToString("HH:mm:ss") ?? "-") + " 시작"
            : s.ScreenStoppedReason ?? "";

        _pending.Text = s.Uploading != null
            ? $"올리는 중: {s.Uploading}  (대기 {s.PendingFiles}개 · {Storage.FmtBytes(s.PendingBytes)})"
            : $"업로드 대기 {s.PendingFiles}개 · {Storage.FmtBytes(s.PendingBytes)}";

        var last = s.LastUploadAt?.ToString("MM-dd HH:mm") ?? "없음";
        _drive.Text = s.DriveLinked
            ? $"Drive 연결됨 · 마지막 업로드 {last}"
            : "Drive 연결 필요 — 파일은 로컬에 쌓이고 있습니다";

        _error.Text = s.LastUploadError ?? "";

        _toggle.Text = s.RecordingEnabled ? "기록 중지 (OFF)" : "기록 시작 (ON)";
        _link.Text = _auth.IsLinked ? "Google 계정 연결 해제" : "Google 계정 연결";
    }

    /// <summary>X 를 눌러도 끝나지 않는다. 트레이로 내려간다.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        RecorderState.Changed -= OnStateChanged;
        _tick.Stop();
        base.OnFormClosing(e);
    }
}
