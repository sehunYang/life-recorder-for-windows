using System.Runtime.InteropServices;
using Windows.Media.Control;

namespace LifeRecorderWin.Capture;

/// <summary>
/// 사람이 손을 안 대도 "보고 있는 중"인지 Windows 에게 묻는다. 유휴 판정(<see cref="IdleWatcher"/>)의 예외다.
///
/// 세 신호 중 하나라도 켜져 있으면 미디어가 도는 것으로 본다.
///  - 미디어 세션: 볼륨 팝업에 뜨는 그것. 크롬·엣지의 YouTube·넷플릭스, Spotify 가 여기 붙는다. 제목까지 준다
///  - 오디오 출력 미터: 소리가 나는 모든 것. 세션에 안 붙는 플레이어(mpv·PotPlayer)와 게임을 잡는다
///  - 전체화면: 전체화면 영상·게임·발표
///
/// 10초에 한 번 본다. 앞 창 기록이 세션의 제목을 media 이벤트로 남긴다 — "무엇을 보고 있었나"가 글자로 남는다.
/// </summary>
internal sealed class MediaWatcher : IDisposable
{
    public sealed record Session(string App, string Title, string Artist, bool Private = false);

    private readonly object _lock = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private IAudioMeterInformation? _meter;
    private int _loudPolls;

    /// <summary>지금 재생 중인 세션들. 세션 API 가 없는 환경이면 비어 있다.</summary>
    public IReadOnlyList<Session> Playing { get; private set; } = Array.Empty<Session>();

    /// <summary>오디오가 두 번 연속(20초) 들렸는가. 알림음 한 번에 흔들리지 않게 한다.</summary>
    public bool AudioActive { get; private set; }

    public bool Fullscreen { get; private set; }

    public bool IsActive => Playing.Count > 0 || AudioActive || Fullscreen;

    /// <summary>상태창·로그용 한 줄. 아무것도 없으면 null.</summary>
    public string? Describe()
    {
        var p = Playing;
        if (p.Count > 0) return "재생 중: " + p[0].App + (p[0].Title.Length > 0 ? " — " + p[0].Title : "");
        if (Fullscreen) return "전체화면";
        if (AudioActive) return "소리 남";
        return null;
    }

    /// <summary>10초마다 부른다. 예외는 삼킨다 — 이게 죽어도 녹화는 계속되어야 한다.</summary>
    public void Poll()
    {
        try { Playing = PollSessions(); } catch (Exception e) { Log.Warn("미디어 세션 조회 실패: " + e.Message); Playing = Array.Empty<Session>(); }
        try
        {
            var loud = Peak() >= Config.MediaAudioPeak;
            _loudPolls = loud ? Math.Min(_loudPolls + 1, 2) : 0;
            AudioActive = _loudPolls >= 2;
        }
        catch (Exception e) { Log.Warn("오디오 미터 실패: " + e.Message); _meter = null; AudioActive = false; }
        try
        {
            var state = SHQueryUserNotificationState(out var q) == 0 ? q : 0;
            // 2 = 전체화면 앱(BUSY), 3 = D3D 전체화면, 4 = 발표 모드
            Fullscreen = state is 2 or 3 or 4;
        }
        catch (Exception) { Fullscreen = false; }
    }

    private IReadOnlyList<Session> PollSessions()
    {
        lock (_lock)
        {
            _manager ??= GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
        }
        var list = new List<Session>();
        foreach (var s in _manager.GetSessions())
        {
            if (s.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
            var app = s.SourceAppUserModelId ?? "";
            string title = "", artist = "";
            if (IsPrivateSource(app))
            {
                // Brave, 또는 시크릿 창이 떠 있는 동안의 Chrome. 어느 창에서 트는지 세션만 봐서는 모르므로
                // 제목을 통째로 뺀다. 재생 중이라는 사실(녹화를 이어갈지)은 그대로 쓴다.
                list.Add(new Session(app, "", "", Private: true));
                continue;
            }
            try
            {
                var props = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                title = props?.Title ?? "";
                artist = props?.Artist ?? "";
            }
            catch (Exception) { }
            list.Add(new Session(app, title, artist));
        }
        return list;
    }

    private static bool IsPrivateSource(string app) =>
        app.Contains("brave", StringComparison.OrdinalIgnoreCase)
        || (app.Contains("chrome", StringComparison.OrdinalIgnoreCase) && PrivateWindows.AnyChromePrivate());

    private float Peak()
    {
        lock (_lock)
        {
            if (_meter == null)
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device));
                var iid = typeof(IAudioMeterInformation).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj));
                _meter = (IAudioMeterInformation)obj;
            }
            Marshal.ThrowExceptionForHR(_meter.GetPeakValue(out var peak));
            return peak;
        }
    }

    public void Dispose()
    {
        lock (_lock) { _meter = null; _manager = null; }
    }

    // ── Win32 / CoreAudio ────────────────────────────────────────────────────

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    // 메서드 순서가 곧 vtable 이다. 쓰는 것까지만, 순서대로 적는다.
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
    }
}
