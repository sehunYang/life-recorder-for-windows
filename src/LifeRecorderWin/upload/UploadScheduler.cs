using LifeRecorderWin.Capture;

namespace LifeRecorderWin.Upload;

/// <summary>
/// 업로드를 언제 돌릴지 정한다. 안드로이드 <c>UploadScheduler.kt</c>(WorkManager) 의 자리다.
///
///  - 세그먼트가 하나 닫힐 때마다
///  - 실패하면 잠깐 뒤에 다시
///  - 아무 일이 없어도 30분마다 한 번 (놓친 파일을 위한 안전망)
///
/// 안드로이드의 "Wi-Fi 전용 / 충전 중에만" 제약은 노트북에서 그대로 뜻이 있어 되살렸다.
/// 배터리로 돌거나 종량제 회선일 때는 미뤘다가, 전원이 꽂히고 회선이 풀리면 몰아서 올린다.
/// "지금 업로드"를 누르면 그 제약을 무시한다 (안드로이드의 수동 버튼과 같다).
/// </summary>
internal sealed class UploadScheduler : IDisposable
{
    private readonly UploadWorker _worker;
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>사용자가 "지금 업로드"를 눌렀다. 이번 한 번은 배터리·회선 제약을 넘긴다.</summary>
    private volatile bool _manual;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public UploadScheduler(DriveAuth auth)
    {
        _worker = new UploadWorker(auth);
    }

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>
    /// 지금 한 번 돌려 달라. 이미 도는 중이면 끝난 뒤 한 번 더 돈다.
    /// <paramref name="manual"/> 이면 배터리·종량제 때문에 미뤄 두는 것을 무시한다.
    /// </summary>
    public void RequestNow(bool manual = false)
    {
        if (manual) _manual = true;
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 이미 예약돼 있다.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // 뜨자마자 한 번. 지난 번에 남은 것부터 치운다.
        var delay = TimeSpan.Zero;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 배터리로 돌거나 데이터 요금이 붙는 회선이면 미뤄 둔다. 녹화는 계속되고 파일은 쌓인다.
            var hold = _manual ? null : HoldReason();
            _manual = false;
            if (hold != null)
            {
                RecorderState.Update(s => s with { UploadHoldReason = hold });
                delay = Config.UploadHoldRecheck;
                continue;
            }
            RecorderState.Update(s => s with { UploadHoldReason = null });

            bool done;
            try
            {
                done = await _worker.RunAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Error("업로드 루프에서 예외: " + e);
                done = false;
            }

            // 성공하면 다음 정기 점검까지, 실패하면 짧게 쉬었다 다시.
            delay = done ? Config.UploadPeriod : Config.UploadRetryDelay;
        }
    }

    /// <summary>지금 올리면 안 되는 이유. null 이면 올려도 된다. 데스크톱에서는 언제나 null 이다.</summary>
    private static string? HoldReason()
    {
        var prefs = Prefs.Current;
        if (prefs.HoldUploadOnBattery && PowerInfo.OnBattery)
        {
            var pct = PowerInfo.BatteryPercent;
            return "배터리로 도는 중" + (pct != null ? $" ({pct}%)" : "");
        }
        if (prefs.HoldUploadOnMetered && PowerInfo.IsMetered()) return "종량제 회선";
        return null;
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // 종료 중이다. 다음 실행에서 이어 올린다.
        }
        _cts.Dispose();
        _signal.Dispose();
    }
}
