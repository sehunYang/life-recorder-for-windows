namespace LifeRecorderWin.Capture;

/// <summary>
/// 입력이 한동안 없으면 "쉬어야 할 이유"를 낸다. 잠금·모니터 꺼짐과 같은 자리다 (<see cref="PowerWatcher"/>).
///
/// 왜 필요한가 — 집 PC는 밤새 켜져 있고 화면도 안 꺼진다. 사람은 없는데 움직이는 배경화면과
/// 시계 때문에 시간당 480MB 가 찍혔다 (2026-09-17 새벽 3시 파일 실측). 담을 것이 없는 시간이다.
/// 입력이 돌아오면 10초 안에 다시 시작한다. 쉰 구간은 앞 창 기록(pcapp_)에 stop/start 로 남는다.
///
/// 예외 — 손을 안 대도 보고 있는 중일 수 있다. 영상·음악이 돌거나 전체화면이면
/// (<see cref="MediaWatcher"/>) 입력이 없어도 쉬지 않는다.
/// </summary>
internal sealed class IdleWatcher : IDisposable
{
    /// <summary>null 이면 다시 담아도 된다. 없음↔있음 전환에만 알린다.</summary>
    public event Action<string?>? PauseReasonChanged;

    /// <summary>10초마다 미디어 상태를 새로 본 직후. 앞 창 기록이 media 이벤트를 남기는 데 쓴다.</summary>
    public event Action? Polled;

    private readonly MediaWatcher _media;
    private readonly System.Threading.Timer _timer;
    private bool _paused;

    public IdleWatcher(MediaWatcher media)
    {
        _media = media;
        _timer = new System.Threading.Timer(_ => Tick(), null, PollMs, PollMs);
    }

    public string? PauseReason { get; private set; }

    private void Tick()
    {
        _media.Poll();
        Polled?.Invoke();

        var idle = TimeSpan.FromMilliseconds(Input.IdleMs());
        var paused = idle >= Config.IdlePauseAfter && !_media.IsActive;
        PauseReason = paused ? $"입력 없음 {(int)idle.TotalMinutes}분" : null;
        if (paused == _paused) return;
        _paused = paused;
        Log.Info(paused
            ? $"입력이 {(int)idle.TotalMinutes}분 없어 화면 녹화를 쉽니다"
            : _media.IsActive && idle >= Config.IdlePauseAfter
                ? $"입력은 없지만 {_media.Describe()} — 화면 녹화를 계속합니다"
                : "입력이 돌아와 화면 녹화를 다시 시작합니다");
        PauseReasonChanged?.Invoke(PauseReason);
    }

    private const int PollMs = 10_000;

    public void Dispose() => _timer.Dispose();
}
