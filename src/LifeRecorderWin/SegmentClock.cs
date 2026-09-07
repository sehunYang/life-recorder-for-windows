namespace LifeRecorderWin;

/// <summary>
/// 세그먼트 경계는 벽시계 정각(HH:00:00)에 맞춘다. 첫 세그먼트만 짧고 이후는 한 시간 단위다.
/// 안드로이드 <c>SegmentClock.kt</c> 와 같은 규칙이다.
///
/// 실제로 파일을 가르고 이름을 붙이는 것은 ffmpeg 쪽이다
/// (<c>-segment_atclocktime 1</c> 과 <c>-strftime 1</c>, <see cref="Capture.ScreenRecorderSession"/> 참고).
/// 여기 있는 것은 "이번 세그먼트가 언제 시작했는지"를 UI 에 보여 주기 위한 같은 계산이다.
/// </summary>
internal static class SegmentClock
{
    /// <summary>지금 쓰고 있는 세그먼트가 시작된 시각. 직전 정각이거나, 그보다 늦게 켰으면 켠 시각이다.</summary>
    public static DateTime SegmentStart(DateTime startedAt, DateTime now)
    {
        var top = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind);
        return startedAt > top ? startedAt : top;
    }
}
