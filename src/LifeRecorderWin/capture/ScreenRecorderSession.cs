using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace LifeRecorderWin.Capture;

/// <summary>
/// gdigrab → H.264 → mp4 세그먼트. 안드로이드 <c>ScreenRecorderSession.kt</c> 의 자리다.
///
/// 안드로이드에서는 MediaProjection → VirtualDisplay → MediaCodec → MediaMuxer 를 직접 붙이고
/// 정각마다 키프레임을 요청해 파일을 갈아탔다. 여기서는 그 일을 ffmpeg 의 segment 먹서가 한다
/// (<c>-segment_atclocktime 1</c>: 벽시계 정각에서 자른다). 프레임이 끊기지 않는 것도 같다.
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
    private System.Threading.Timer? _sweep;
    private volatile bool _stopping;
    private readonly List<string> _stderrTail = new();

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

        var args = BuildArgs(rect);
        var psi = new ProcessStartInfo(exe)
        {
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 'q' 를 넣어 정상 종료시키기 위해 stdin 을 잡는다. 이래야 마지막 세그먼트에 moov 가 쓰인다.
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

            // 닫힌 세그먼트를 주기적으로 대기열로 옮긴다.
            _sweep = new System.Threading.Timer(_ => Sweep(), null, SweepPeriod, SweepPeriod);

            Log.Info($"화면 녹화 시작 {rect.Width}x{rect.Height} → {CaptureSize} "
                     + $"@{Config.ScreenFps}fps, 상한 {Bitrate(rect) / 1000}kbps");
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
    /// 정상 종료를 요청한다. ffmpeg 이 stdin 에서 <c>q</c> 를 받으면 쓰던 세그먼트를 닫고 나간다.
    /// 안 나가면 죽인다 (그 세그먼트는 moov 가 없어 버려진다).
    /// </summary>
    public void Stop()
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

        if (p != null && !p.HasExited)
        {
            try
            {
                p.StandardInput.Write("q");
                p.StandardInput.Flush();
            }
            catch (Exception e)
            {
                Log.Warn("ffmpeg 종료 요청 실패: " + e.Message);
            }

            if (!p.WaitForExit(QuitTimeoutMs))
            {
                Log.Warn("ffmpeg 이 제때 끝나지 않아 강제 종료합니다 (마지막 세그먼트는 버려집니다)");
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                p.WaitForExit(2000);
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
    private const int QuitTimeoutMs = 8_000;

    /// <summary>
    /// gdigrab 의 <c>desktop</c> 은 기본값이 **주 모니터 하나**다 (SM_CXSCREEN).
    /// 모니터를 전부 붙인 한 프레임으로 담으려면 가상 화면의 원점과 크기를 직접 넘겨야 한다.
    /// 왼쪽·위쪽에 붙인 모니터가 있으면 원점이 음수가 된다.
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
        if (Math.Abs(Config.ScreenScale - 1.0) < 0.001) return (rect.Width, rect.Height);
        var w = Math.Max(16, (int)(rect.Width * Config.ScreenScale) / 2 * 2);
        var h = Math.Max(16, (int)(rect.Height * Config.ScreenScale) / 2 * 2);
        return (w, h);
    }

    private static int Bitrate(Rectangle rect)
    {
        var (w, h) = OutputSize(rect);
        var raw = (double)w * h * Config.ScreenFps * Config.ScreenBitsPerPixelPerFrame;
        return (int)Math.Clamp(raw, Config.ScreenMinBitrate, Config.ScreenMaxBitrate);
    }

    private static string BuildArgs(Rectangle rect)
    {
        var bitrate = Bitrate(rect);
        var pattern = Path.Combine(Storage.WorkDir, Config.ScreenPrefix + "%Y-%m-%d_%H-%M-%S.mp4");
        var inv = CultureInfo.InvariantCulture;

        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "warning",

            // 입력: 가상 데스크톱 전체.
            "-f", "gdigrab",
            "-framerate", Config.ScreenFps.ToString(inv),
            "-draw_mouse", Config.ScreenDrawMouse ? "1" : "0",
            "-offset_x", rect.Left.ToString(inv),
            "-offset_y", rect.Top.ToString(inv),
            "-video_size", $"{rect.Width}x{rect.Height}",
            "-i", "desktop",
        };

        if (Math.Abs(Config.ScreenScale - 1.0) > 0.001)
        {
            var (w, h) = OutputSize(rect);
            a.AddRange(new[] { "-vf", $"scale={w}:{h}:flags=bicubic" });
        }

        a.AddRange(new[]
        {
            // 인코딩: 정지 화면에서는 비트를 거의 안 쓰고(CRF), 움직일 때만 상한까지(maxrate).
            "-c:v", Config.ScreenEncoder,
            "-preset", Config.ScreenPreset,
            // 프레임 버퍼링을 없앤다. 2fps 에서는 이게 없으면 세그먼트 경계가 20초 가까이 밀린다.
            "-tune", Config.ScreenTune,
            "-crf", Config.ScreenCrf.ToString(inv),
            "-maxrate", bitrate.ToString(inv),
            "-bufsize", (bitrate * 2).ToString(inv),
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
        if (string.IsNullOrWhiteSpace(e.Data)) return;
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
        _sweep?.Dispose();
        _sweep = null;
        Stopped?.Invoke(reason);
    }

    public void Dispose() => Stop();
}
