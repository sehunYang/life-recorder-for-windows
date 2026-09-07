using System.Text.Json;
using System.Text.Json.Serialization;

namespace LifeRecorderWin;

/// <summary>
/// 설정과 진행 상태. 안드로이드 <c>Prefs.kt</c> 와 같은 자리다.
/// 비밀은 여기 두지 않는다 (그건 <see cref="Credentials"/>).
/// </summary>
internal sealed class Prefs
{
    /// <summary>사용자가 ON 을 눌러 둔 상태인가. 재부팅·재시작 뒤 이 값을 보고 되살린다.</summary>
    public bool RecordingEnabled { get; set; }

    /// <summary>
    /// 이 컴퓨터를 가리키는 짧은 이름 (<c>home</c>, <c>school</c>). 올라가는 파일 이름 끝에 붙는다.
    /// 정해지기 전에는 녹화를 시작하지 않는다 — 두 대가 같은 이름으로 올리면
    /// <c>pcindex_&lt;날짜&gt;.jsonl</c> 이 매일 충돌한다.
    /// </summary>
    public string DeviceName { get; set; } = "";

    /// <summary>Windows 로그인 시 자동 시작.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>종량제 회선(핫스팟·LTE)에서는 업로드를 미룬다. 데스크톱에서는 걸릴 일이 없다.</summary>
    public bool HoldUploadOnMetered { get; set; } = true;

    /// <summary>
    /// 배터리로 돌 때는 업로드를 미룬다. 녹화는 그대로 계속한다.
    /// 배터리가 없는 컴퓨터에서는 아무 일도 하지 않는다.
    /// </summary>
    public bool HoldUploadOnBattery { get; set; } = true;

    /// <summary>Drive 폴더 ID 캐시. 매번 이름으로 찾지 않기 위한 것.</summary>
    public Dictionary<string, string> FolderIds { get; set; } = new();

    /// <summary>파일 이름 → 재개 가능 업로드 세션 URI. 끊긴 업로드를 이어 붙이는 데 쓴다.</summary>
    public Dictionary<string, string> UploadSessions { get; set; } = new();

    public DateTime? LastUploadAtUtc { get; set; }

    // ── 저장·불러오기 ────────────────────────────────────────────────────────

    private static readonly object Lock = new();
    private static Prefs? _current;

    private static string FilePath => Path.Combine(Storage.BaseDir, "prefs.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Prefs Current
    {
        get
        {
            lock (Lock)
            {
                if (_current != null) return _current;
                try
                {
                    _current = File.Exists(FilePath)
                        ? JsonSerializer.Deserialize<Prefs>(File.ReadAllText(FilePath)) ?? new Prefs()
                        : new Prefs();
                }
                catch (Exception e)
                {
                    Log.Warn("prefs 읽기 실패, 기본값으로 시작: " + e.Message);
                    _current = new Prefs();
                }
                return _current;
            }
        }
    }

    public static void Save()
    {
        lock (Lock)
        {
            if (_current == null) return;
            try
            {
                Directory.CreateDirectory(Storage.BaseDir);
                // 쓰다가 죽어도 이전 설정이 남도록 임시 파일에 쓰고 바꿔치기한다.
                var tmp = FilePath + Storage.Part;
                File.WriteAllText(tmp, JsonSerializer.Serialize(_current, JsonOpts));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception e)
            {
                Log.Warn("prefs 저장 실패: " + e.Message);
            }
        }
    }

    public static void Update(Action<Prefs> f)
    {
        lock (Lock)
        {
            f(Current);
        }
        Save();
    }

    // ── 자주 쓰는 것들 ───────────────────────────────────────────────────────

    public static string? FolderId(string key) =>
        Current.FolderIds.TryGetValue(key, out var v) ? v : null;

    public static void SetFolderId(string key, string id) => Update(p => p.FolderIds[key] = id);

    /// <summary>사용자가 Drive 에서 폴더를 지웠을 수 있다. 캐시를 비우고 다음에 다시 찾는다.</summary>
    public static void ClearFolderIds() => Update(p => p.FolderIds.Clear());

    public static string? UploadSession(string name) =>
        Current.UploadSessions.TryGetValue(name, out var v) ? v : null;

    public static void SetUploadSession(string name, string? uri) => Update(p =>
    {
        if (uri == null) p.UploadSessions.Remove(name);
        else p.UploadSessions[name] = uri;
    });

    /// <summary>대기열에 없는 파일의 세션은 고아다. 지운다.</summary>
    public static void PruneUploadSessions(IReadOnlySet<string> pending) => Update(p =>
    {
        foreach (var k in p.UploadSessions.Keys.Where(k => !pending.Contains(k)).ToList())
            p.UploadSessions.Remove(k);
    });
}
