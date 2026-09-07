using LifeRecorderWin.Upload;

namespace LifeRecorderWin.Capture;

/// <summary>
/// 화면 녹화를 붙들고 있는 조정자. 안드로이드 <c>RecordingService.kt</c> 의 자리다.
///
/// 사용자가 OFF 를 누르기 전까지는 어떤 이유로도 멈춘 채로 두지 않는다.
/// ffmpeg 이 죽으면 점점 간격을 늘려 가며 계속 다시 붙는다.
/// 잠금·모니터 꺼짐·절전 동안에는 담을 게 없으니 쉬었다가, 풀리면 바로 다시 시작한다.
/// </summary>
internal sealed class RecordingService : IDisposable
{
    private readonly object _lock = new();
    private readonly PowerWatcher _power;
    private readonly UploadScheduler _uploads;

    private ScreenRecorderSession? _session;
    private System.Threading.Timer? _retry;
    private int _retryStep;

    private static readonly int[] RetryDelaysMs = { 10_000, 30_000, 60_000, 300_000 };

    public RecordingService(UploadScheduler uploads)
    {
        _uploads = uploads;
        _power = new PowerWatcher();
        _power.PauseReasonChanged += OnPauseReasonChanged;
        _power.DisplayLayoutChanged += OnDisplayLayoutChanged;
    }

    /// <summary>앱이 뜰 때 한 번. 저장된 ON/OFF 상태를 그대로 이어받는다.</summary>
    public void Restore()
    {
        RecorderState.Update(s => s with { RecordingEnabled = Prefs.Current.RecordingEnabled });
        if (Prefs.Current.RecordingEnabled)
        {
            Log.Info("저장된 상태가 ON 이라 녹화를 이어서 시작합니다");
            Apply();
        }
        else
        {
            // 꺼져 있어도 지난 번에 남은 세그먼트는 정리해서 올려 둔다.
            Storage.RecoverWorkDir();
            RecorderState.RefreshPending();
        }
    }

    public void SetEnabled(bool enabled)
    {
        Prefs.Update(p => p.RecordingEnabled = enabled);
        RecorderState.Update(s => s with
        {
            RecordingEnabled = enabled,
            ScreenStoppedReason = null,
        });
        Log.Info(enabled ? "녹화 ON" : "녹화 OFF");
        Apply();
    }

    /// <summary>지금 있어야 할 상태로 맞춘다. 어느 스레드에서 불러도 된다.</summary>
    private void Apply()
    {
        lock (_lock)
        {
            var enabled = Prefs.Current.RecordingEnabled;
            var pause = _power.PauseReason;
            var shouldRun = enabled && pause == null;

            RecorderState.Update(s => s with { ScreenPausedReason = enabled ? pause : null });

            if (shouldRun && _session == null) StartSession();
            else if (!shouldRun && _session != null) StopSession();
        }
    }

    private void StartSession()
    {
        var session = new ScreenRecorderSession();
        session.SegmentFinished += OnSegmentFinished;
        session.Stopped += OnSessionStopped;

        if (!session.Start(out var error))
        {
            RecorderState.Update(s => s with { ScreenRecording = false, ScreenStoppedReason = error });
            ScheduleRetry();
            return;
        }

        _session = session;
        _retryStep = 0;
        CancelRetry();
        RecorderState.Update(s => s with
        {
            ScreenRecording = true,
            ScreenStoppedReason = null,
            CaptureSize = session.CaptureSize,
            CurrentSegmentStart = SegmentClock.SegmentStart(session.StartedAt, DateTime.Now),
        });
        // 시작하면서 지난 번에 남은 것을 대기열에 올렸을 수 있다.
        _uploads.RequestNow();
    }

    private void StopSession()
    {
        var s = _session;
        _session = null;
        CancelRetry();
        s?.Stop();
        s?.Dispose();
        RecorderState.Update(x => x with { ScreenRecording = false, CurrentSegmentStart = null });
        RecorderState.RefreshPending();
        _uploads.RequestNow();
    }

    private void OnSegmentFinished(string name)
    {
        RecorderState.RefreshPending();
        _uploads.RequestNow();
    }

    /// <summary>ffmpeg 이 우리 뜻과 무관하게 끝났다. 켜져 있는 한 다시 붙는다.</summary>
    private void OnSessionStopped(string reason)
    {
        lock (_lock)
        {
            _session = null;
            RecorderState.Update(s => s with { ScreenRecording = false, ScreenStoppedReason = reason });
            RecorderState.RefreshPending();
            _uploads.RequestNow();
            if (Prefs.Current.RecordingEnabled) ScheduleRetry();
        }
    }

    private void ScheduleRetry()
    {
        var delay = RetryDelaysMs[Math.Min(_retryStep, RetryDelaysMs.Length - 1)];
        _retryStep++;
        Log.Warn($"{delay / 1000}초 뒤 화면 녹화를 다시 시도합니다 (시도 {_retryStep}회째)");
        CancelRetry();
        _retry = new System.Threading.Timer(_ => Apply(), null, delay, Timeout.Infinite);
    }

    private void CancelRetry()
    {
        _retry?.Dispose();
        _retry = null;
    }

    private void OnPauseReasonChanged(string? reason) => Apply();

    /// <summary>
    /// 모니터가 붙거나 빠지면 프레임 크기가 달라진다. ffmpeg 은 입력 크기를 도중에 못 바꾸므로
    /// 세션을 닫고 새 크기로 다시 연다. 그 자리에서 세그먼트가 하나 갈린다.
    /// </summary>
    private void OnDisplayLayoutChanged()
    {
        lock (_lock)
        {
            if (_session == null) return;
            var now = ScreenRecorderSession.VirtualScreenRect();
            var before = _session.CaptureRect;
            if (now == before) return;
            Log.Info($"잡는 영역 변경 {before.Width}x{before.Height} → {now.Width}x{now.Height}, 세션을 다시 엽니다");
            StopSession();
        }
        Apply();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            CancelRetry();
            var s = _session;
            _session = null;
            s?.Stop();
            s?.Dispose();
        }
        _power.PauseReasonChanged -= OnPauseReasonChanged;
        _power.DisplayLayoutChanged -= OnDisplayLayoutChanged;
        _power.Dispose();
    }
}
