using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LifeRecorderWin.Upload;

/// <summary>
/// Drive 에 올린 파일을 하루 단위 JSONL 로 남기는 수집 기록. 안드로이드 <c>IndexLog.kt</c> 와 같다.
///
/// <code>
///   index\ rawpcindex_yyyy-MM-dd.jsonl.part  ← 오늘치, 계속 이어 쓰는 중 (업로드 대상 아님)
///   queue\ pcindex_yyyy-MM-dd.jsonl          ← 날이 바뀌어 확정된 것, 업로드 대상
/// </code>
///
/// 목적은 안드로이드와 같다. **무엇이 언제 수집됐는지를 파일이 지워진 뒤에도 남기는 것.**
/// 보관 기간이 지나 Drive 에서 원본을 지워도 이 기록은 남는다.
///
/// 접두어가 <c>pcindex_</c> 인 이유: 안드로이드의 <c>IndexRestore</c> 는 <c>index_</c> 로 시작하는
/// 파일만 읽는다. 같은 <c>index/</c> 폴더를 쓰면서도 서로의 기록을 건드리지 않는다.
/// </summary>
internal static class IndexLog
{
    private static readonly object Lock = new();

    /// <summary>업로드 하나가 끝날 때마다 한 줄. 실패해도 업로드는 계속되어야 하므로 조용히 넘어간다.</summary>
    public static void Record(string name, string folder, long bytes, string driveId, string? md5)
    {
        var now = DateTimeOffset.Now;
        var record = new Dictionary<string, object?>
        {
            ["kind"] = "upload",
            ["t"] = now.ToUnixTimeMilliseconds(),
            ["name"] = name,
            ["folder"] = folder,
            ["bytes"] = bytes,
            ["driveId"] = driveId,
            ["md5"] = md5,
            // 안드로이드는 원본 식별자(call:/camera:)를 넣는 자리다. PC 는 직접 만든 파일뿐이라 없다.
            ["src"] = null,
            // 같은 폴더에 여러 기기 기록이 섞이므로 어느 쪽이 남긴 줄인지 밝혀 둔다.
            ["device"] = Storage.DeviceName,
        };

        lock (Lock)
        {
            try
            {
                var path = Path.Combine(Storage.IndexDir, Storage.RawIndexName(Storage.Today()));
                File.AppendAllText(path, JsonSerializer.Serialize(record, Storage.JsonlOptions) + "\n", Storage.Utf8NoBom);
            }
            catch (Exception e)
            {
                Log.Warn("수집 기록 append 실패: " + e.Message);
            }
        }
    }

    /// <summary>
    /// 날이 지난 기록을 업로드 대상으로 확정한다. 내용은 손대지 않고 자리만 옮긴다.
    /// </summary>
    /// <returns>확정한 파일 수</returns>
    public static int FinalizeCompletedDays()
    {
        lock (Lock) return Storage.FinalizeDailyRaw(Config.RawIndexPrefix, Storage.IndexName);
    }
}
