using System.Globalization;

namespace LifeRecorderWin;

/// <summary>
/// 글자 기록(<c>pcapp_</c> · <c>pcscreentext_</c>)을 **한 시간 조각**으로 확정하기 위한 이름과 구간.
/// 안드로이드 <c>HourSlice.kt</c> 와 같은 규칙이다.
///
/// 전에는 날이 바뀌어야 하루치 파일로 확정했다. 그래서 맥 쪽 배치가 PC 에서 한 일을 다음 날에야 알았다.
/// 2026-10-01 부터 오늘치는 정각마다 지난 한 시간을 <c>pcapp_yyyy-MM-dd_&lt;기기&gt;_hHH.jsonl</c> 로 확정한다
/// (<c>pcapp_2026-10-01_home_h13.jsonl</c> = 13:00~14:00, 로컬 시각).
///
/// 조각 키는 글자 순서가 곧 시간 순서다 (<c>2026-10-01</c> &lt; <c>2026-10-01_h00</c> &lt; <c>2026-10-01_h13</c> &lt; <c>2026-10-02</c>).
///
/// 정각 직후 <see cref="Grace"/> 동안은 지난 시간을 아직 닫지 않는다. 그 시간 끝에 쓰던 줄이 조금 늦게
/// 붙는 것을 기다린다. 이 때문에 정각에 도는 업로드는 지난 시간을 못 올리므로, 업로드 일정은
/// 정각 <see cref="Config.UploadTailDelay"/> 뒤에 한 번씩 돌게 잡는다 (<see cref="Upload.UploadScheduler"/>).
/// </summary>
internal static class HourSlice
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    /// <summary>그 시각이 속한 한 시간 조각의 키. <c>2026-10-01_h13</c></summary>
    public static string Key(DateTime local) =>
        local.ToString("yyyy-MM-dd'_h'HH", CultureInfo.InvariantCulture);

    /// <summary>이 키보다 앞선 조각은 닫혔다 — 확정해 올려도 된다.</summary>
    public static string ClosedBefore(DateTime now) => Key(now - Grace);

    /// <summary>
    /// 지금 뒤로 가장 가까운 "정각 + <paramref name="delay"/>". 지난 시간이 닫힌 뒤 업로드를 한 번 돌릴 시각이다.
    /// </summary>
    public static DateTime NextTail(DateTime now, TimeSpan delay)
    {
        var top = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind);
        var at = top + delay;
        return at > now ? at : at.AddHours(1);
    }
}
