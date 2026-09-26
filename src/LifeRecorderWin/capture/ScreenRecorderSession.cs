using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace LifeRecorderWin.Capture;

/// <summary>
/// 화면(<see cref="ScreenGrabber"/>) → stdin → ffmpeg → H.264 → mp4 세그먼트. 안드로이드 <c>ScreenRecorderSession.kt</c> 의 자리다.
///
/// 안드로이드에서는 MediaProjection → VirtualDisplay → MediaCodec → MediaMuxer 를 직접 붙이고
/// 정각마다 키프레임을 요청해 파일을 갈아탔다. 여기서는 그 일을 ffmpeg 의 segment 먹서가 한다
/// (<c>-segment_atclocktime 1</c>: 벽시계 정각에서 자른다). 프레임이 끊기지 않는 것도 같다.
///
/// 화면은 우리가 떠서 넘긴다. 가릴 창(Brave·Chrome 시크릿)을 파일에 들어가기 전에 검게 칠하려면
/// 프레임이 우리 손을 거쳐야 하기 때문이다. ffmpeg 은 인코딩과 분할만 한다.
///
/// 파일은 <c>work\</c> 에 만들어지고, 닫힌 것만 <c>queue\</c> 로 올라가 업로드 대상이 된다.
/// </summary>
internal sealed class ScreenRecorderSession : IDisposable
{
    /// <summary>세그먼트 하나가 완성돼 업로드 대기열에 들어갔다.</summary>
    public event Action<string>? SegmentFinished;

    /// <summary>우리가 멈춘 게 아니라 밖에서 끝났다.</summary>
    public event Action<string>? Stopped;

    private readonly object _lock = new();
    private Process? _proc;
    private Thread? _grabThread;
    private readonly ManualResetEventSlim _stopGrab = new();
    private System.Threading.Timer? _sweep;
    private volatile bool _stopping;
    private readonly List<string> _stderrTail = new();
    private string _encoder = "libx264";

    /// <summary>이만큼 안에 죽으면 인코더 탓으로 보고 다음 인코더로 내려간다.</summary>
    private static readonly TimeSpan EarlyDeath = TimeSpan.FromSeconds(30);

    public DateTime StartedAt { get; private set; }

    /// <summary>실제로 잡고 있는 가상 데스크톱 영역. 모니터 구성이 바뀌었는지 판단하는 기준이다.</summary>
    public Rectangle CaptureRect { get; private set; }

    /// <summary>파일에 들어가는 프레임 크기. 축소했다면 <see cref="CaptureRect"/> 보다 작다.</summary>
    public string CaptureSize { get; private set; } = "";

    /// <summary>
    /// 시작한다. 실패하면 예외 대신 false 를 돌려주고 이유를 <paramref name="error"/> 에 담는다.
    /// </summary>
    public bool Start(out string? error)
    {
        error = null;
        var exe = Ffmpeg.Find();
        if (exe == null)
        {
            error = "ffmpeg 을 찾지 못했습니다. scripts\\get-ffmpeg.ps1 을 한 번 실행하세요";
            return false;
        }

        if (!Storage.HasDeviceName)
        {
            // 이름 없이 올리면 두 대째부터 파일 이름이 겹친다. 정해질 때까지 시작하지 않는다.
            error = "이 컴퓨터의 이름을 먼저 정해 주세요 (예: home, school)";
            return false;
        }

        // 시작 전에 지난 번에 남은 것을 정리한다. 이 시점의 work\ 는 전부 죽은 파일이다.
        var (promoted, dropped) = Storage.RecoverWorkDir();
        if (promoted > 0 || dropped > 0)
            Log.Info($"이전 세션 정리: 살림 {promoted}개, 버림 {dropped}개");

        var rect = VirtualScreenRect();
        if (rect.Width < 16 || rect.Height < 16)
        {
            error = "화면 크기를 얻지 못했습니다";
            return false;
        }

        _encoder = Encoders.Choose(exe);
        var args = BuildArgs(rect, _encoder);
        var psi = new ProcessStartInfo(exe)
        {
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 프레임을 stdin 으로 넣는다. 멈출 때는 stdin 을 닫는다 — ffmpeg 이 입력 끝으로 알고 마지막 세그먼트에 moov 를 쓴다.
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        try
        {
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.ErrorDataReceived += OnStderr;
            p.Exited += OnExited;
            if (!p.Start())
            {
                error = "ffmpeg 을 시작하지 못했습니다";
                return false;
            }
            p.BeginErrorReadLine();

            lock (_lock)
            {
                _proc = p;
                _stopping = false;
                StartedAt = DateTime.Now;
                CaptureRect = rect;
                // 파일에 실제로 들어가는 크기를 들고 있는다. 잡는 크기는 이보다 크다.
                var (w, h) = OutputSize(rect);
                CaptureSize = $"{w}x{h}";
            }

            ScreenGrabber grabber;
            try
            {
                grabber = new ScreenGrabber(rect);
            }
            catch (Exception e)
            {
                // 우리가 끝내는 것이다. OnExited 가 "예기치 않은 종료"로 재시도를 한 번 더 걸지 않게 한다.
                lock (_lock)
                {
                    _stopping = true;
                    _proc = null;
                }
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                error = e.Message;
                return false;
            }
            _stopGrab.Reset();
            _grabThread = new Thread(() => GrabLoop(p, grabber))
            {
                IsBackground = true,
                Name = "screen-grab",
                Priority = ThreadPriority.AboveNormal,
            };
            _grabThread.Start();

            // 닫힌 세그먼트를 주기적으로 대기열로 옮긴다.
            _sweep = new System.Threading.Timer(_ => Sweep(), null, SweepPeriod, SweepPeriod);

            Log.Info($"화면 녹화 시작 [{Storage.DeviceName}] {rect.Width}x{rect.Height} → {CaptureSize} "
                     + $"@{Config.ScreenFps}fps, {_encoder}, 상한 {Bitrate(rect) / 1000}kbps "
                     + $"(화면 배율 {Dpi.SystemScalePercent()})");
            return true;
        }
        catch (Exception e)
        {
            error = "ffmpeg 실행 실패: " + e.Message;
            Log.Error(error);
            return false;
        }
    }

    /// <summary>
    /// 정상 종료를 요청한다. 화면 뜨기를 멈추고 stdin 을 닫으면 ffmpeg 이 쓰던 세그먼트를 닫고 나간다.
    /// 안 나가면 죽인다 (그 세그먼트는 moov 가 없어 버려진다).
    /// </summary>
    /// <param name="timeoutMs">
    /// stdin 을 닫은 뒤에 기다려 줄 시간. 시스템 종료 중에는 Windows 가 몇 초만 기다려 주므로
    /// <see cref="ShutdownQuitTimeoutMs"/> 로 짧게 부른다 (ffmpeg 이 세그먼트를 닫는 데는 보통 1초가 안 걸린다).
    /// </param>
    public void Stop(int timeoutMs = QuitTimeoutMs)
    {
        Process? p;
        lock (_lock)
        {
            _stopping = true;
            p = _proc;
            _proc = null;
        }
        _sweep?.Dispose();
        _sweep = null;

        // 쓰던 프레임은 마저 넘기게 기다린다. 도중에 끊으면 ffmpeg 이 반쪽 프레임을 받는다.
        // 기다리는 시간은 ffmpeg 을 기다리는 것과 합쳐 timeoutMs 안에 든다 (시스템 종료 중에는 몇 초뿐이다).
        var deadline = Environment.TickCount64 + timeoutMs;
        _stopGrab.Set();
        var t = _grabThread;
        _grabThread = null;
        if (t != null && !t.Join(Math.Min(timeoutMs / 2, 1500)))
            Log.Warn("화면 뜨기가 제때 멈추지 않았습니다");

        if (p != null && !p.HasExited)
        {
            try
            {
                p.StandardInput.Close();
            }
            catch (Exception e)
            {
                Log.Warn("ffmpeg 종료 요청 실패: " + e.Message);
            }

            if (!p.WaitForExit((int)Math.Max(200, deadline - Environment.TickCount64)))
            {
                Log.Warn("ffmpeg 이 제때 끝나지 않아 강제 종료합니다 (마지막 세그먼트는 버려집니다)");
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                p.WaitForExit(1000);
            }
        }
        p?.Dispose();

        // 멈춘 뒤에는 work\ 를 전수 검사할 수 있다. 정상적으로 닫힌 것은 전부 살린다.
        var (promoted, dropped) = Storage.RecoverWorkDir();
        if (dropped > 0) Log.Info($"미완성 세그먼트 {dropped}개 버림");
        if (promoted > 0)
        {
            Log.Info($"세그먼트 {promoted}개 대기열로");
            SegmentFinished?.Invoke("");
        }
        RecorderState.Update(s => s with { ScreenRecording = false, CurrentSegmentStart = null });
    }

    // ── 내부 ─────────────────────────────────────────────────────────────────

    private const int SweepPeriod = 10_000;

    /// <summary>
    /// 밀린 만큼 같은 장을 되풀이해 넣는 상한. 파일의 시간이 벽시계와 맞도록 되풀이하지만,
    /// 한 장이 수십 MB 라 몰아서 넣지는 않는다 — ffmpeg 이 느려서 밀린 것이면 되풀이한 장이 그만큼 더 밀리게 한다.
    /// 넘는 만큼은 파일에서 시간이 빠진다.
    /// </summary>
    private const int MaxCatchUpFrames = 2;
    private const int QuitTimeoutMs = 8_000;
    /// <summary>시스템 종료 중 기다려 줄 시간. Windows 의 응답 대기(기본 5초)보다 짧아야 강제 종료를 피한다.</summary>
    public const int ShutdownQuitTimeoutMs = 3_000;

    /// <summary>
    /// 모니터를 전부 붙인 가상 화면. 왼쪽·위쪽에 붙인 모니터가 있으면 원점이 음수가 된다.
    /// </summary>
    public static Rectangle VirtualScreenRect()
    {
        var v = SystemInformation.VirtualScreen;
        // yuv420p 는 가로·세로가 짝수여야 한다. 남으면 1픽셀 깎는다.
        return new Rectangle(v.Left, v.Top, v.Width - (v.Width % 2), v.Height - (v.Height % 2));
    }

    /// <summary>
    /// 파일에 실제로 들어가는 프레임 크기. yuv420p 라 가로·세로 모두 짝수여야 한다.
    /// </summary>
    private static (int w, int h) OutputSize(Rectangle rect)
    {
        var scale = Dpi.EffectiveScale;
        if (Math.Abs(scale - 1.0) < 0.001) return (rect.Width, rect.Height);
        var w = Math.Max(16, (int)(rect.Width * scale) / 2 * 2);
        var h = Math.Max(16, (int)(rect.Height * scale) / 2 * 2);
        return (w, h);
    }

    private static int Bitrate(Rectangle rect)
    {
        var (w, h) = OutputSize(rect);
        var raw = (double)w * h * Config.ScreenFps * Config.ScreenBitsPerPixelPerFrame;
        return (int)Math.Clamp(raw, Config.ScreenMinBitrate, Config.ScreenMaxBitrate);
    }

    private static string BuildArgs(Rectangle rect, string encoder)
    {
        var bitrate = Bitrate(rect);
        var pattern = Storage.ScreenPattern();
        var inv = CultureInfo.InvariantCulture;

        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "warning",

            // 입력: 가상 데스크톱 전체를 우리가 떠서 stdin 으로 (ScreenGrabber). 가릴 창은 이미 검다.
            "-f", "rawvideo",
            // 프레임률을 알려 주므로 재 보지 않게 한다. 한 장이 수십 MB 라 재려 들면 매번 "not enough frames" 경고가 뜬다.
            "-fpsprobesize", "0",
            "-pix_fmt", "bgr0",
            "-video_size", $"{rect.Width}x{rect.Height}",
            "-framerate", Config.ScreenFps.ToString(inv),
            "-i", "pipe:0",
        };

        if (Math.Abs(Dpi.EffectiveScale - 1.0) > 0.001)
        {
            var (w, h) = OutputSize(rect);
            a.AddRange(new[] { "-vf", $"scale={w}:{h}:flags=bicubic" });
        }

        // 인코딩: 정지 화면에서는 비트를 거의 안 쓰고, 움직일 때만 상한까지. 인코더마다 옵션이 다르다.
        a.AddRange(Encoders.Args(encoder, bitrate));
        a.AddRange(new[]
        {
            "-pix_fmt", "yuv420p",

            // 세그먼트는 키프레임에서만 갈린다. 정각 경계 오차의 상한이 이 간격이다.
            "-force_key_frames", $"expr:gte(t,n_forced*{Config.ScreenKeyframeSec})",
            "-an",

            // 출력: 벽시계 정각마다 새 mp4. 첫 세그먼트만 짧다 (안드로이드와 같은 규칙).
            "-f", "segment",
            "-segment_time", Config.SegmentSeconds.ToString(inv),
            "-segment_atclocktime", "1",
            "-reset_timestamps", "1",
            "-segment_format", "mp4",
            "-strftime", "1",
            pattern,
        });

        return string.Join(" ", a.Select(Quote));
    }

    private static string Quote(string s) =>
        s.Contains(' ') || s.Contains('%') ? "\"" + s + "\"" : s;

    /// <summary>
    /// 초당 <see cref="Config.ScreenFps"/> 장을 떠서 stdin 으로 넣는다. ffmpeg 은 들어온 장 수로 시간을 세므로
    /// n 번째 장은 n/fps 초에 떠야 한다. 뜨는 게 늦어지면 밀린 만큼 같은 장을 되풀이해 시간을 맞춘다.
    /// ffmpeg 이 죽으면 쓰기가 실패하고 여기서 나간다 — 그 뒤는 <see cref="OnExited"/> 가 맡는다.
    /// </summary>
    private void GrabLoop(Process p, ScreenGrabber grabber)
    {
        using var _ = grabber;
        var period = 1000.0 / Config.ScreenFps;
        var clock = Stopwatch.StartNew();
        long n = 0;
        try
        {
            var stdin = p.StandardInput.BaseStream;
            while (!_stopGrab.IsSet)
            {
                var frame = grabber.Grab();
                var due = (long)(clock.Elapsed.TotalMilliseconds / period);
                if (due - n >= MaxCatchUpFrames) n = due - MaxCatchUpFrames + 1;
                do
                {
                    stdin.Write(frame, 0, frame.Length);
                    n++;
                } while (n <= due && !_stopGrab.IsSet);
                stdin.Flush();

                var wait = n * period - clock.Elapsed.TotalMilliseconds;
                if (wait > 0) _stopGrab.Wait(TimeSpan.FromMilliseconds(wait));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (_stopping) return;
            Log.Warn("ffmpeg 에 프레임을 넘기지 못했습니다: " + e.Message);
            // ffmpeg 이 죽은 게 아니라 뜨는 쪽 오류였으면 ffmpeg 은 기다리기만 한다. 닫아서 끝낸다.
            try { p.StandardInput.Close(); } catch (Exception) { }
        }
        catch (Exception e)
        {
            Log.Error("화면 뜨기 실패: " + e);
            // 프레임이 끊기면 ffmpeg 은 기다리기만 한다. 닫아서 끝내고, 다시 붙는 것은 OnExited 에 맡긴다.
            try { p.StandardInput.Close(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// <c>work\</c> 에서 가장 최근 것 하나(= 지금 쓰는 중)를 뺀 나머지는 이미 닫힌 세그먼트다.
    /// 대기열로 옮기고 업로드를 깨운다.
    /// </summary>
    private void Sweep()
    {
        try
        {
            var moved = Storage.PromoteClosedSegments();
            if (moved.Count > 0)
            {
                foreach (var name in moved) Log.Info("세그먼트 완료: " + name);
                SegmentFinished?.Invoke(moved[^1]);
            }
            RecorderState.Update(s => s with
            {
                CurrentSegmentStart = SegmentClock.SegmentStart(StartedAt, DateTime.Now),
            });
        }
        catch (Exception e)
        {
            Log.Warn("세그먼트 정리 실패: " + e.Message);
        }
    }

    private void OnStderr(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data) || Encoders.IsNoise(e.Data)) return;
        Log.Warn("ffmpeg: " + e.Data);
        lock (_stderrTail)
        {
            _stderrTail.Add(e.Data);
            if (_stderrTail.Count > 5) _stderrTail.RemoveAt(0);
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (_stopping) return;
        string tail;
        lock (_stderrTail) tail = _stderrTail.Count > 0 ? _stderrTail[^1] : "";
        var reason = string.IsNullOrEmpty(tail) ? "ffmpeg 이 종료되었습니다" : "ffmpeg: " + tail;
        Log.Error("화면 녹화가 예기치 않게 끝났습니다 — " + reason);
        // 켜자마자 죽었으면 하드웨어 인코더 탓일 가능성이 크다. 다시 붙을 때는 다음 인코더로.
        if (DateTime.Now - StartedAt < EarlyDeath) Encoders.MarkBroken(_encoder);
        _sweep?.Dispose();
        _sweep = null;
        Stopped?.Invoke(reason);
    }

    public void Dispose()
    {
        Stop();
        _stopGrab.Dispose();
    }
}
