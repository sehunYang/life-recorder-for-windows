namespace LifeRecorderWin;

/// <summary>
/// 녹화·업로드 품질과 동작을 결정하는 상수. 필요하면 여기만 바꾸면 된다.
/// 안드로이드 쪽 <c>Config.kt</c> 와 같은 자리다.
/// </summary>
internal static class Config
{
    // ── 화면 녹화 ────────────────────────────────────────────────────────────
    // 가상 데스크톱 전체(모니터를 전부 붙인 하나의 프레임)를 원본 해상도 그대로 초당 2장.
    // 폰과 달리 PC는 글씨가 작아서 해상도를 줄이면 비전 모델이 못 읽는다.
    // fps 보다 해상도가 판독에 훨씬 중요해서 fps 를 2로 낮추고 해상도를 살렸다.

    /// <summary>1.0 이면 원본 그대로. 줄이려면 0.75 같은 값을 넣는다.</summary>
    public const double ScreenScale = 1.0;

    public const int ScreenFps = 2;

    /// <summary>
    /// 1920x1080 @ 2fps 를 1.5Mbps 로 본 기준을 "픽셀·프레임당 비트"로 환산한 값.
    /// 모니터를 붙여 프레임이 넓어지면 상한도 같은 비율로 올라간다.
    /// (1080p 두 대를 붙인 3840x1080 이면 3.0Mbps)
    /// </summary>
    public const double ScreenBitsPerPixelPerFrame = 1_500_000.0 / (1920.0 * 1080.0 * ScreenFps);

    public const int ScreenMinBitrate = 800_000;
    public const int ScreenMaxBitrate = 8_000_000;

    /// <summary>
    /// 품질 기준값. 정지 화면에서는 비트를 거의 쓰지 않고, 움직일 때만 위 상한까지 쓴다.
    /// 안드로이드에서 VBR 로 얻던 동작을 x264 에서는 CRF + maxrate 로 만든다.
    /// </summary>
    public const int ScreenCrf = 26;

    public const string ScreenPreset = "veryfast";

    /// <summary>
    /// 인코더가 프레임을 쌓아 두지 않게 한다. 빼면 안 된다.
    ///
    /// x264 의 프레임 스레딩과 lookahead 는 지연을 **프레임 수**로 센다.
    /// 보통의 30fps 라면 30프레임 지연이 1초지만, 우리는 2fps 라서 같은 지연이 **15초 넘게** 된다.
    /// 세그먼트를 자를지 판단하는 시점이 그만큼 밀려서, 파일 이름의 시각이 실제 내용보다
    /// 20초 가까이 늦어졌다 (실측). 정각 분할이 이름으로 드러나야 하므로 지연을 없앤다.
    /// B프레임과 lookahead 를 포기하는 대신 파일이 조금 커진다.
    /// </summary>
    public const string ScreenTune = "zerolatency";

    /// <summary>하드웨어 인코더를 쓰려면 h264_nvenc / h264_qsv / h264_amf 로 바꾼다.</summary>
    public const string ScreenEncoder = "libx264";

    /// <summary>키프레임 간격(초). 세그먼트는 키프레임에서만 갈리므로 경계 오차의 상한이기도 하다.</summary>
    public const int ScreenKeyframeSec = 5;

    /// <summary>마우스 커서를 그릴지. 무엇을 가리키고 있었는지가 남는다.</summary>
    public const bool ScreenDrawMouse = true;

    /// <summary>세그먼트 길이. 벽시계 정각 경계에 맞춰 끊는다 (안드로이드와 동일).</summary>
    public const int SegmentSeconds = 3600;

    // ── 업로드 ───────────────────────────────────────────────────────────────

    /// <summary>Google Drive 재개 가능 업로드 청크. 256KB 배수여야 한다.</summary>
    public const int UploadChunkBytes = 8 * 1024 * 1024;

    /// <summary>놓친 파일이 있어도 이 간격마다 한 번은 시도하는 안전망.</summary>
    public static readonly TimeSpan UploadPeriod = TimeSpan.FromMinutes(30);

    /// <summary>업로드가 실패했을 때 다시 시도하기까지.</summary>
    public static readonly TimeSpan UploadRetryDelay = TimeSpan.FromMinutes(1);

    // ── Drive 배치 ───────────────────────────────────────────────────────────
    // 안드로이드가 쓰는 폴더를 그대로 쓴다. 폴더를 새로 만들지 않는 것이 요구사항이다.

    public const string DriveRootFolder = "LifeRecorder";
    public const string DriveScreenFolder = "screen";
    public const string DriveIndexFolder = "index";

    /// <summary>같은 폴더에 섞이므로 접두어로 기기를 구분한다. 안드로이드는 <c>screen_</c> 다.</summary>
    public const string ScreenPrefix = "pcscreen_";

    /// <summary>확정된 수집 기록. 안드로이드의 <c>IndexRestore</c> 는 <c>index_</c> 만 읽으므로 서로 간섭하지 않는다.</summary>
    public const string IndexPrefix = "pcindex_";

    /// <summary>오늘치라 아직 올리지 않는 수집 기록.</summary>
    public const string RawIndexPrefix = "rawpcindex_";

    // ── Google OAuth ─────────────────────────────────────────────────────────

    /// <summary>
    /// 안드로이드는 <c>drive.file</c>(자기가 만든 파일만)을 쓰지만, 그 스코프로는
    /// 다른 클라이언트가 만든 <c>LifeRecorder</c> 폴더가 보이지 않아 같은 이름의 폴더를 하나 더 만들게 된다.
    /// 기존 폴더에 그대로 넣기 위해 데스크톱 쪽은 전체 <c>drive</c> 스코프를 쓴다.
    /// </summary>
    public const string OAuthScope = "https://www.googleapis.com/auth/drive";

    public const string OAuthAuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string OAuthTokenEndpoint = "https://oauth2.googleapis.com/token";
}
