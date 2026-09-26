using System.Diagnostics;
using System.Globalization;

namespace LifeRecorderWin.Capture;

/// <summary>
/// 이 컴퓨터에서 쓸 H.264 인코더를 고른다. <see cref="Config.ScreenEncoders"/> 를 앞에서부터
/// ffmpeg 으로 두 장 인코딩해 보고, 되는 첫 번째를 쓴다. 결과는 앱이 도는 동안 들고 있는다.
///
/// 하드웨어 인코더는 그 GPU 가 있는 컴퓨터에서만 된다 (h264_amf 는 AMD). 없으면 ffmpeg 이 바로 실패하므로
/// libx264 로 내려간다. 시험은 통과했는데 실제 녹화가 곧바로 죽으면(<see cref="MarkBroken"/>) 그것도 뺀다.
/// </summary>
internal static class Encoders
{
    private static readonly object Lock = new();
    private static string? _chosen;
    private static readonly HashSet<string> Broken = new();

    /// <summary>쓸 인코더 이름. 처음 부를 때 시험하느라 1~2초 걸릴 수 있다.</summary>
    public static string Choose(string ffmpeg)
    {
        lock (Lock)
        {
            if (_chosen != null && !Broken.Contains(_chosen)) return _chosen;
            foreach (var enc in Config.ScreenEncoders)
            {
                if (Broken.Contains(enc)) continue;
                if (enc == "libx264" || Works(ffmpeg, enc))
                {
                    _chosen = enc;
                    Log.Info("인코더: " + enc);
                    return enc;
                }
                Log.Info($"인코더 {enc} 를 이 컴퓨터에서 쓸 수 없어 다음 것을 봅니다");
            }
            _chosen = "libx264";
            return _chosen;
        }
    }

    /// <summary>이 인코더로 연 녹화가 곧바로 죽었다. 앱이 도는 동안은 다시 고르지 않는다.</summary>
    public static void MarkBroken(string encoder)
    {
        if (encoder == "libx264") return;
        lock (Lock)
        {
            if (!Broken.Add(encoder)) return;
            if (_chosen == encoder) _chosen = null;
        }
        Log.Warn($"인코더 {encoder} 로 연 녹화가 곧바로 끝나 다음부터 쓰지 않습니다");
    }

    /// <summary>인코딩 옵션. 정지 화면에서는 비트를 거의 안 쓰고, 움직일 때만 상한(bitrate)까지 쓴다.</summary>
    public static IEnumerable<string> Args(string encoder, int bitrate)
    {
        var inv = CultureInfo.InvariantCulture;
        var rate = bitrate.ToString(inv);
        var buf = (bitrate * 2).ToString(inv);
        if (encoder == "h264_amf")
            return new[]
            {
                "-c:v", "h264_amf",
                "-usage", "transcoding",
                "-quality", "quality",
                // 상한이 있는 VBR 에 품질 하한(qmin)을 건다. 하한이 없으면 정지 화면에도 목표 비트를 채워 쓰고,
                // CQP 는 상한이 없어 바쁜 화면에서 10Mbps 를 넘는다 (2026-09-26 실측, Config.ScreenAmfQmin).
                "-rc", "vbr_peak",
                "-b:v", rate,
                "-maxrate", rate,
                "-bufsize", buf,
                "-qmin", Config.ScreenAmfQmin.ToString(inv),
                // B 프레임이 있으면 프레임을 쌓아 둔다. 2fps 에서는 그게 세그먼트 경계를 밀어낸다 (x264 의 zerolatency 와 같은 이유).
                // 키프레임 간격은 못 정한다. 이 드라이버는 -g 를 무시하고 30장(15초)마다 넣는다 (2026-09-26 실측,
                // -g 0~1000·usage 를 바꿔도 같았다). 그 몫을 넣고도 정지 화면 용량은 libx264 보다 작았고,
                // 정각 분할 오차가 최대 15초로 줄어드는 덤이 있다.
                "-bf", "0",
            };
        return new[]
        {
            "-c:v", "libx264",
            "-preset", Config.ScreenPreset,
            // 프레임 버퍼링을 없앤다. 2fps 에서는 이게 없으면 세그먼트 경계가 20초 가까이 밀린다.
            "-tune", Config.ScreenTune,
            "-crf", Config.ScreenCrf.ToString(inv),
            "-maxrate", rate,
            "-bufsize", buf,
        };
    }

    /// <summary>
    /// 알고도 어쩔 수 없는 ffmpeg 경고. 녹화를 시작할 때마다 찍혀 로그를 흐린다.
    /// AMD 내장 그래픽은 H.264 B 프레임을 못 만든다는 안내다 — 우리는 애초에 B 프레임을 끈다.
    /// </summary>
    public static bool IsNoise(string line) =>
        line.Contains("does not support H.264 B-frame", StringComparison.Ordinal);

    private static bool Works(string ffmpeg, string encoder)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                Arguments = $"-hide_banner -loglevel error -f lavfi -i color=s=320x240:r=2 -frames:v 2 -c:v {encoder} -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var err = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return false;
            }
            if (p.ExitCode != 0)
            {
                var msg = err.Result.Trim();
                if (msg.Length > 0) Log.Info($"인코더 {encoder} 시험 실패: " + msg.Split('\n')[^1]);
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"인코더 {encoder} 시험 실패: " + e.Message);
            return false;
        }
    }
}
