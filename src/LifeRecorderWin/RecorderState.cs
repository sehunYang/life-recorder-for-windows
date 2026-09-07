namespace LifeRecorderWin;

/// <summary>UI 와 백그라운드 작업이 함께 보는 상태. 안드로이드 <c>RecorderState.kt</c> 와 같은 자리다.</summary>
internal sealed record Status
{
    /// <summary>사용자가 ON 을 눌러 둔 상태인가.</summary>
    public bool RecordingEnabled { get; init; }

    /// <summary>ffmpeg 이 실제로 프레임을 받고 있는가.</summary>
    public bool ScreenRecording { get; init; }

    /// <summary>잠금·화면 꺼짐·절전으로 잠시 멈춘 이유. null 이면 안 멈췄다.</summary>
    public string? ScreenPausedReason { get; init; }

    /// <summary>의도치 않게 끝난 이유 (ffmpeg 이 죽는 등).</summary>
    public string? ScreenStoppedReason { get; init; }

    public DateTime? CurrentSegmentStart { get; init; }

    /// <summary>실제로 잡고 있는 프레임 크기. "3840x1080" 꼴.</summary>
    public string? CaptureSize { get; init; }

    public int PendingFiles { get; init; }
    public long PendingBytes { get; init; }

    public string? Uploading { get; init; }

    /// <summary>배터리·종량제 때문에 업로드를 미뤄 둔 이유. null 이면 안 미뤘다.</summary>
    public string? UploadHoldReason { get; init; }
    public DateTime? LastUploadAt { get; init; }
    public string? LastUploadError { get; init; }
    public bool DriveLinked { get; init; }
}

internal static class RecorderState
{
    private static readonly object Lock = new();
    private static Status _status = new();

    public static Status Current
    {
        get { lock (Lock) return _status; }
    }

    /// <summary>UI 스레드가 아닌 곳에서도 올라온다. 받는 쪽에서 마셜링할 것.</summary>
    public static event Action<Status>? Changed;

    public static void Update(Func<Status, Status> f)
    {
        Status next;
        lock (Lock)
        {
            next = f(_status);
            if (next == _status) return;
            _status = next;
        }
        Changed?.Invoke(next);
    }

    /// <summary>디스크를 읽으므로 UI 스레드에서 부르지 말 것.</summary>
    public static void RefreshPending()
    {
        var files = Storage.FinishedFiles();
        var bytes = files.Sum(f => f.Length);
        Update(s => s with { PendingFiles = files.Count, PendingBytes = bytes });
    }
}
