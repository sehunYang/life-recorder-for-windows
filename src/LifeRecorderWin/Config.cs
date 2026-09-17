namespace LifeRecorderWin;

/// <summary>
/// 녹화·업로드 품질과 동작을 결정하는 상수. 필요하면 여기만 바꾸면 된다.
/// 안드로이드 쪽 <c>Config.kt</c> 와 같은 자리다.
/// </summary>
internal static class Config
{
    // ── 화면 녹화 ────────────────────────────────────────────────────────────
    // 가상 데스크톱 전체(모니터를 전부 붙인 하나의 프레임)를 초당 2장.
    // 폰과 달리 PC는 글씨가 작아서 fps 보다 해상도가 판독에 훨씬 중요하다. fps 를 2로 낮추고 해상도를 살렸다.

    /// <summary>
    /// **논리 해상도 대비** 목표 크기. 실제 축소 배율은 화면 배율을 읽어 여기서 계산한다
    /// (<see cref="Capture.Dpi.EffectiveScale"/>).
    ///
    /// 왜 물리 픽셀이 아니라 논리 해상도를 기준으로 잡는가 —
    /// 캡처는 DPI 인식 상태라 입력이 언제나 물리 픽셀인데, 그 크기는 같은 화면이라도
    /// Windows 배율 설정에 따라 달라진다. 판독을 좌우하는 것은 **글자 하나가 결과 영상에서
    /// 몇 픽셀을 차지하느냐**이고, 그건 논리 해상도에 비례한다.
    /// 이 값을 고정해 두면 배율이 다른 컴퓨터끼리 글씨 크기가 같아진다.
    ///
    /// 0.75 인 근거는 150% 배율 3화면(물리 5760x2172, 논리 3840x1448)에서의 실측이다 —
    ///
    ///   논리 1.0  (3840x1448)  시간당 약 930MB
    ///   논리 0.75 (2880x1086)  시간당 약 563MB   ← 한글·코드가 그대로 읽힌다
    ///   물리 1.0  (5760x2172)  시간당 약 1.9GB   ← 위와 눈에 띄는 차이 없음
    ///
    /// 셋 다 판독에 차이가 없어 가장 싼 것을 골랐다. 더 또렷하게 남기고 싶으면 1.0 으로 올린다.
    /// </summary>
    public const double ScreenTargetLogicalScale = 0.75;

    /// <summary>
    /// 배율 계산을 무시하고 물리 픽셀 대비 배율을 직접 박고 싶을 때. 0 이면 자동.
    /// </summary>
    // const 로 두면 0 비교가 컴파일 시점에 접혀서 자동 계산 쪽이 죽은 코드로 잡힌다.
    public static readonly double ScreenScaleOverride = 0;

    public const int ScreenFps = 2;

    /// <summary>
    /// 1920x1080 @ 2fps 를 1.5Mbps 로 본 기준을 "픽셀·프레임당 비트"로 환산한 값.
    /// 축소한 뒤의 프레임이 넓어지면 상한도 같은 비율로 올라간다.
    ///
    /// 이건 **상한**일 뿐이고 평소 용량을 정하는 것은 <see cref="ScreenCrf"/> 다.
    /// 정지 화면에서는 CRF 쪽이 훨씬 낮은 값을 고르기 때문에 상한에 닿지 않는다.
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

    /// <summary>
    /// 키프레임 간격(초). 세그먼트는 키프레임에서만 갈리므로 경계 오차의 상한이기도 하다.
    ///
    /// 5초였을 때 정지 화면에서 시간당 240MB 가 키프레임만으로 나갔다 (2880x1086, 장당 340KB × 720장,
    /// 2026-09-17 새벽 파일 실측). CRF 는 I-프레임을 못 줄인다. 30초로 늘려 그 몫을 6분의 1로 만든다.
    /// 대신 정각 분할이 최대 30초 늦을 수 있다 (DESIGN.md 6절).
    /// </summary>
    public const int ScreenKeyframeSec = 30;

    /// <summary>
    /// 입력이 이만큼 없으면 화면 녹화를 쉰다 (<see cref="Capture.IdleWatcher"/>).
    /// 밤새 켜 둔 PC 에서 움직이는 배경화면과 시계만 찍히던 것을 막는다. 입력이 오면 10초 안에 재개.
    /// </summary>
    public static readonly TimeSpan IdlePauseAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 오디오 출력 피크(0~1)가 이 값 이상이면 "소리가 난다"로 본다 (<see cref="Capture.MediaWatcher"/>).
    /// 20초 연속일 때만 인정해 알림음 한 번에는 흔들리지 않는다. 영상·음악이 돌면 입력이 없어도 녹화를 계속한다.
    /// </summary>
    public const float MediaAudioPeak = 0.02f;

    /// <summary>마우스 커서를 그릴지. 무엇을 가리키고 있었는지가 남는다.</summary>
    public const bool ScreenDrawMouse = true;

    /// <summary>세그먼트 길이. 벽시계 정각 경계에 맞춰 끊는다 (안드로이드와 동일).</summary>
    public const int SegmentSeconds = 3600;

    // ── 앞 창 기록 ───────────────────────────────────────────────────────────
    // 어느 앱이 앞에 있었는지를 영상 옆에 색인으로 남긴다. 내려받은 쪽이 프레임을 읽지 않고도
    // "이 구간은 무슨 앱"을 알게 하려는 것이다 (Capture/ActiveWindowLog.cs).

    /// <summary>앞 창을 확인하는 간격. 바뀌었을 때만 적으므로 자주 봐도 파일은 안 는다.</summary>
    public const int AppPollMs = 1000;

    /// <summary>이만큼 입력이 없으면 자리를 비운 것으로 본다.</summary>
    public const int AppIdleAfterMs = 60_000;

    /// <summary>창 제목은 이 길이에서 자른다. 브라우저 탭 제목이 길다.</summary>
    public const int AppTitleMaxLength = 200;

    // ── 업로드 ───────────────────────────────────────────────────────────────

    /// <summary>Google Drive 재개 가능 업로드 청크. 256KB 배수여야 한다.</summary>
    public const int UploadChunkBytes = 8 * 1024 * 1024;

    /// <summary>놓친 파일이 있어도 이 간격마다 한 번은 시도하는 안전망.</summary>
    public static readonly TimeSpan UploadPeriod = TimeSpan.FromMinutes(30);

    /// <summary>업로드가 실패했을 때 다시 시도하기까지.</summary>
    public static readonly TimeSpan UploadRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 배터리·종량제 때문에 업로드를 미뤄 둔 동안 다시 확인하는 간격.
    ///
    /// 안드로이드의 "Wi-Fi 전용 / 충전 중에만" 제약을 노트북용으로 되살린 것이고,
    /// **미루는 것은 업로드뿐이다.** 배터리로도 녹화는 계속한다 —
    /// 수업 중 노트북 화면이야말로 남기고 싶은 것이라 그때 꺼져 있으면 앱을 쓰는 뜻이 없다.
    /// 배터리가 없는 컴퓨터에서는 어느 것도 걸리지 않는다.
    /// </summary>
    public static readonly TimeSpan UploadHoldRecheck = TimeSpan.FromMinutes(2);

    // ── Drive 배치 ───────────────────────────────────────────────────────────
    // 안드로이드가 쓰는 폴더를 그대로 쓴다. 폴더를 새로 만들지 않는 것이 요구사항이다.

    public const string DriveRootFolder = "LifeRecorder";
    public const string DriveScreenFolder = "screen";
    public const string DriveIndexFolder = "index";

    /// <summary>앞 창 기록. 안드로이드의 앱 사용 기록(<c>app_</c>)과 같은 폴더를 쓴다.</summary>
    public const string DriveAppFolder = "app";

    /// <summary>같은 폴더에 섞이므로 접두어로 기기를 구분한다. 안드로이드는 <c>screen_</c> 다.</summary>
    public const string ScreenPrefix = "pcscreen_";

    /// <summary>확정된 수집 기록. 안드로이드의 <c>IndexRestore</c> 는 <c>index_</c> 만 읽으므로 서로 간섭하지 않는다.</summary>
    public const string IndexPrefix = "pcindex_";

    /// <summary>오늘치라 아직 올리지 않는 수집 기록.</summary>
    public const string RawIndexPrefix = "rawpcindex_";

    /// <summary>확정된 앞 창 기록. 안드로이드는 <c>app_</c> 다.</summary>
    public const string AppPrefix = "pcapp_";

    /// <summary>오늘치라 아직 올리지 않는 앞 창 기록.</summary>
    public const string RawAppPrefix = "rawpcapp_";

    /// <summary>
    /// 기기 이름의 최대 길이. 파일 이름 끝에 <c>_home</c> 처럼 붙어 컴퓨터를 가른다.
    ///
    /// 두 대 이상에서 쓰면 이게 없을 때 <c>pcindex_&lt;날짜&gt;.jsonl</c> 이 **매일 충돌한다.**
    /// Drive 는 같은 이름을 그냥 두 개 만들어 버려서 어느 컴퓨터 것인지 알 수 없게 된다.
    /// </summary>
    public const int DeviceNameMaxLength = 16;

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
