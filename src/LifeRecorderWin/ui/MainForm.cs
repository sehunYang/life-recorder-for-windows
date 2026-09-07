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
    private readonly TextBox _device = new();
    private readonly Button _deviceSave = new();
    private readonly CheckBox _autoStart = new();
    private readonly CheckBox _holdMetered = new();
    private readonly CheckBox _holdBattery = new();
    private readonly System.Windows.Forms.Timer _tick = new();

    public MainForm(RecordingService recording, UploadScheduler uploads, DriveAuth auth)
    {
        _recording = recording;
        _uploads = uploads;
        _auth = auth;

        Text = "Life Recorder";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        AutoScaleMode = AutoScaleMode.None;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = Px.Z(460, 500);
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
        _state.Location = Px.P(20, 18);
        _state.Size = Px.Z(420, 32);

        _screen.Location = Px.P(20, 54);
        _screen.Size = Px.Z(420, 20);
        _screen.ForeColor = Color.FromArgb(90, 90, 90);

        var sep1 = new Label { BorderStyle = BorderStyle.Fixed3D, Location = Px.P(20, 86), Size = Px.Z(420, 2) };

        _pending.Location = Px.P(20, 100);
        _pending.Size = Px.Z(420, 20);

        _drive.Location = Px.P(20, 124);
        _drive.Size = Px.Z(420, 20);

        _error.Location = Px.P(20, 148);
        _error.Size = Px.Z(420, 36);
        _error.ForeColor = Color.FromArgb(180, 40, 40);

        _toggle.Location = Px.P(20, 196);
        _toggle.Size = Px.Z(200, 44);
        _toggle.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _toggle.Click += (_, _) => _recording.SetEnabled(!Prefs.Current.RecordingEnabled);

        _upload.Text = "지금 업로드";
        _upload.Location = Px.P(240, 196);
        _upload.Size = Px.Z(200, 44);
        _upload.Click += (_, _) =>
        {
            _uploads.RequestNow(manual: true);
            RecorderState.Update(s => s with { LastUploadError = null });
        };

        _link.Location = Px.P(20, 252);
        _link.Size = Px.Z(200, 34);
        _link.Click += async (_, _) => await ToggleLinkAsync();

        var openFolder = new Button
        {
            Text = "로컬 폴더 열기",
            Location = Px.P(240, 252),
            Size = Px.Z(200, 34),
        };
        openFolder.Click += (_, _) => LinkDialog.OpenUrl(Storage.BaseDir);

        var deviceLabel = new Label
        {
            Text = "컴퓨터 이름",
            Location = Px.P(20, 304),
            Size = Px.Z(88, 22),
        };
        _device.Location = Px.P(110, 301);
        _device.Size = Px.Z(120, 24);
        _device.MaxLength = Config.DeviceNameMaxLength;
        _device.Text = Prefs.Current.DeviceName;

        _deviceSave.Text = "저장";
        _deviceSave.Location = Px.P(238, 300);
        _deviceSave.Size = Px.Z(70, 26);
        _deviceSave.Click += (_, _) => SaveDeviceName();

        var deviceHint = new Label
        {
            Text = "예: home, school",
            Location = Px.P(316, 304),
            Size = Px.Z(124, 22),
            ForeColor = Color.FromArgb(110, 110, 110),
        };

        _autoStart.Text = "Windows 시작할 때 자동으로 실행";
        _autoStart.Location = Px.P(20, 340);
        _autoStart.Size = Px.Z(420, 24);
        _autoStart.Checked = AutoStart.IsEnabled();
        _autoStart.CheckedChanged += (_, _) =>
        {
            AutoStart.Set(_autoStart.Checked);
            Prefs.Update(p => p.AutoStart = _autoStart.Checked);
        };

        // 노트북용. 배터리가 없는 컴퓨터에서는 켜 둬도 아무 일도 일어나지 않는다.
        _holdMetered.Text = "종량제 회선(핫스팟·LTE)에서는 업로드 미루기";
        _holdMetered.Location = Px.P(20, 366);
        _holdMetered.Size = Px.Z(420, 24);
        _holdMetered.Checked = Prefs.Current.HoldUploadOnMetered;
        _holdMetered.CheckedChanged += (_, _) =>
        {
            Prefs.Update(p => p.HoldUploadOnMetered = _holdMetered.Checked);
            _uploads.RequestNow();
        };

        // 미루는 것은 업로드뿐이다. 배터리로도 녹화는 계속한다.
        _holdBattery.Text = "배터리로 돌 때는 업로드 미루기 (녹화는 계속)";
        _holdBattery.Location = Px.P(20, 392);
        _holdBattery.Size = Px.Z(420, 24);
        _holdBattery.Checked = Prefs.Current.HoldUploadOnBattery;
        _holdBattery.CheckedChanged += (_, _) =>
        {
            Prefs.Update(p => p.HoldUploadOnBattery = _holdBattery.Checked);
            _uploads.RequestNow();
        };

        var note = new Label
        {
            Text = "컴퓨터 이름은 올라가는 파일 이름 끝에 붙어 어느 컴퓨터 화면인지 가릅니다.\n"
                   + "잠금·모니터 꺼짐·절전 동안에는 쉬었다가, 풀리면 바로 다시 시작합니다.\n"
                   + "업로드가 끝난 파일은 로컬에서 지웁니다. 창을 닫아도 트레이에 남습니다.",
            Location = Px.P(20, 424),
            Size = Px.Z(420, 56),
            ForeColor = Color.FromArgb(110, 110, 110),
        };

        Controls.AddRange(new Control[]
        {
            _state, _screen, sep1, _pending, _drive, _error,
            _toggle, _upload, _link, openFolder,
            deviceLabel, _device, _deviceSave, deviceHint,
            _autoStart, _holdMetered, _holdBattery, note,
        });
    }

    /// <summary>
    /// 기기 이름을 저장한다. 이 이름이 올라가는 파일 이름 끝에 붙어 컴퓨터를 가른다.
    /// 이미 이 이름으로 올린 파일이 있는데 바꾸면 한 컴퓨터가 두 이름으로 보이므로 한 번 확인을 받는다.
    /// </summary>
    private void SaveDeviceName()
    {
        var clean = Storage.SanitizeDeviceName(_device.Text);
        if (clean.Length == 0)
        {
            MessageBox.Show(this, "영문·숫자로 된 짧은 이름을 넣어 주세요. 예: home, school", "Life Recorder");
            return;
        }

        var before = Storage.DeviceName;
        if (before.Length > 0 && before != clean)
        {
            var confirm = MessageBox.Show(this,
                $"이름을 \"{before}\" 에서 \"{clean}\" 으로 바꿉니다.\n\n"
                + "이미 올라간 파일은 예전 이름 그대로 남아서, 나중에 데이터를 볼 때\n"
                + "이 컴퓨터가 두 이름으로 보이게 됩니다. 계속할까요?",
                "Life Recorder", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (confirm != DialogResult.OK)
            {
                _device.Text = before;
                return;
            }
        }

        Prefs.Update(p => p.DeviceName = clean);
        _device.Text = clean;
        Log.Info("컴퓨터 이름: " + clean);
        _recording.Reapply();
        Render(RecorderState.Current);
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
        else if (!Storage.HasDeviceName)
        {
            _state.Text = "컴퓨터 이름을 정해 주세요";
            _state.ForeColor = Color.FromArgb(190, 130, 20);
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
        _drive.Text = !s.DriveLinked
            ? "Drive 연결 필요 — 파일은 로컬에 쌓이고 있습니다"
            : s.UploadHoldReason != null
                ? $"업로드 미룸 — {s.UploadHoldReason} · 마지막 업로드 {last}"
                : $"Drive 연결됨 · 마지막 업로드 {last}";

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
