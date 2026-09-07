namespace LifeRecorderWin.Upload;

/// <summary>
/// 업로드를 언제 돌릴지 정한다. 안드로이드 <c>UploadScheduler.kt</c>(WorkManager) 의 자리다.
///
///  - 세그먼트가 하나 닫힐 때마다
///  - 실패하면 잠깐 뒤에 다시
///  - 아무 일이 없어도 30분마다 한 번 (놓친 파일을 위한 안전망)
///
/// 안드로이드에는 Wi-Fi 전용 / 충전 중에만 같은 제약이 있었지만, 데스크톱에서는 뜻이 없어 뺐다.
/// </summary>
internal sealed class UploadScheduler : IDisposable
{
    private readonly UploadWorker _worker;
    private readonly SemaphoreSlim _signal = new(0, 1);
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

    /// <summary>지금 한 번 돌려 달라. 이미 도는 중이면 끝난 뒤 한 번 더 돈다.</summary>
    public void RequestNow()
    {
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
