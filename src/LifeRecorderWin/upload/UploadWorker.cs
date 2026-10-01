namespace LifeRecorderWin.Upload;

/// <summary>
/// 완성된 파일을 오래된 것부터 Drive 에 올리고, 크기·MD5 가 맞으면 로컬에서 지운다.
/// 청크 단위 재개가 가능하므로 도중에 끊겨도 다음 실행에서 이어 올린다.
/// 안드로이드 <c>UploadWorker.kt</c> 를 옮긴 것이다.
/// </summary>
internal sealed class UploadWorker
{
    private readonly DriveAuth _auth;
    private readonly HttpClient _http;

    public UploadWorker(DriveAuth auth)
    {
        _auth = auth;
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            // 청크 하나(8MB)를 느린 회선으로 올려도 넉넉하도록.
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    /// <returns>true 면 대기열을 비웠다. false 면 다시 시도해야 한다.</returns>
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        // 닫힌 기록을 먼저 확정해서 이번 차례에 같이 올린다.
        // 수집 기록은 날이 지난 것, 앞 창 기록·화면 글자는 정각 1분이 지난 한 시간치다.
        IndexLog.FinalizeCompletedDays();
        Capture.ActiveWindowLog.FinalizeCompleted();
        Capture.ScreenTextLog.FinalizeCompleted();
        RecorderState.RefreshPending();

        string token;
        try
        {
            token = await _auth.GetAccessTokenAsync(ct);
        }
        catch (DriveAuth.NotLinkedException e)
        {
            // 업로드는 실패하면 1분 뒤 다시 온다. 매번 남기면 로그가 그것만으로 찬다.
            if (RecorderState.Current.DriveLinked) Log.Warn("업로드 중단 — " + e.Message);
            RecorderState.Update(s => s with { DriveLinked = false, LastUploadError = e.Message });
            return false;
        }
        catch (Exception e)
        {
            RecorderState.Update(s => s with { LastUploadError = e.Message });
            Log.Warn("토큰 갱신 실패: " + e.Message);
            return false;
        }

        RecorderState.Update(s => s with { DriveLinked = true });
        var client = new DriveClient(_http, token);

        try
        {
            var folders = await EnsureFoldersAsync(client, ct);

            var pending = Storage.FinishedFiles().Select(f => f.Name).ToHashSet();
            Prefs.PruneUploadSessions(pending);

            while (!ct.IsCancellationRequested)
            {
                var file = Storage.FinishedFiles().FirstOrDefault();
                if (file == null) break;

                RecorderState.Update(s => s with { Uploading = file.Name, LastUploadError = null });
                if (!await UploadOneAsync(client, file, folders, ct)) return false;

                RecorderState.Update(s => s with { Uploading = null, LastUploadAt = DateTime.Now });
                Prefs.Update(p => p.LastUploadAtUtc = DateTime.UtcNow);
                RecorderState.RefreshPending();
            }

            RecorderState.Update(s => s with { Uploading = null });
            return !ct.IsCancellationRequested;
        }
        catch (DriveClient.AuthException e)
        {
            _auth.InvalidateAccessToken();
            RecorderState.Update(s => s with { Uploading = null, LastUploadError = e.Message });
            return false;
        }
        catch (DriveClient.NotFoundException e)
        {
            // 사용자가 Drive 에서 폴더를 지웠을 수 있다. 캐시를 비우고 다음에 다시 찾는다.
            Log.Warn("폴더를 찾지 못했습니다. 캐시를 비웁니다: " + e.Message);
            Prefs.ClearFolderIds();
            RecorderState.Update(s => s with { Uploading = null, LastUploadError = e.Message });
            return false;
        }
        catch (OperationCanceledException)
        {
            RecorderState.Update(s => s with { Uploading = null });
            return false;
        }
        catch (Exception e)
        {
            Log.Warn("업로드 실패: " + e.Message);
            RecorderState.Update(s => s with { Uploading = null, LastUploadError = e.Message });
            return false;
        }
    }

    /// <summary>
    /// 안드로이드가 이미 만들어 둔 <c>LifeRecorder/screen</c> 과 <c>LifeRecorder/index</c> 를 찾는다.
    /// 폴더를 새로 만들지 않는 것이 핵심이라, 이름으로 찾을 수 있는 전체 <c>drive</c> 스코프를 쓴다.
    /// (없으면 만든다 — 이 PC 가 먼저인 경우)
    /// </summary>
    private static async Task<Dictionary<string, string>> EnsureFoldersAsync(DriveClient client, CancellationToken ct)
    {
        var root = Prefs.FolderId("root");
        if (root == null)
        {
            root = await client.EnsureFolderAsync(Config.DriveRootFolder, null, ct);
            Prefs.SetFolderId("root", root);
            Log.Info("Drive 루트 폴더: " + root);
        }

        var result = new Dictionary<string, string>();
        foreach (var (key, name) in new[]
                 {
                     ("screen", Config.DriveScreenFolder),
                     ("index", Config.DriveIndexFolder),
                     ("app", Config.DriveAppFolder),
                     ("screentext", Config.DriveScreenTextFolder),
                 })
        {
            var id = Prefs.FolderId(key);
            if (id == null)
            {
                id = await client.EnsureFolderAsync(name, root, ct);
                Prefs.SetFolderId(key, id);
                Log.Info($"Drive 폴더 {name}: {id}");
            }
            result[key] = id;
        }
        return result;
    }

    /// <returns>true 면 완료(로컬 삭제됨). false 면 중단 요청으로 멈춘 것. 오류는 예외로 올라간다.</returns>
    private static async Task<bool> UploadOneAsync(
        DriveClient client, FileInfo file, IReadOnlyDictionary<string, string> folders, CancellationToken ct)
    {
        var folderKey = Storage.FolderKeyOf(file.Name);
        var parent = folders[folderKey];
        var total = file.Length;

        var session = Prefs.UploadSession(file.Name);
        var offset = 0L;
        DriveClient.Uploaded? result = null;

        if (session != null)
        {
            try
            {
                switch (await client.QueryStatusAsync(session, total, ct))
                {
                    case DriveClient.Progress.Continue c:
                        offset = c.NextOffset;
                        break;
                    case DriveClient.Progress.Done d:
                        result = d.Uploaded;
                        break;
                }
            }
            catch (DriveClient.SessionGoneException)
            {
                session = null;
            }
        }

        if (session == null)
        {
            session = await client.StartSessionAsync(file, parent, Storage.MimeOf(file.Name), ct);
            Prefs.SetUploadSession(file.Name, session);
            offset = 0L;
        }

        Log.Info($"업로드 {file.Name} {offset}/{total}");
        while (result == null)
        {
            if (ct.IsCancellationRequested) return false;
            switch (await client.UploadChunkAsync(session, file, offset, total, ct))
            {
                case DriveClient.Progress.Continue c:
                    // 서버가 같은 Range 만 반복하면 같은 청크를 무한 재전송하게 되므로 끊는다.
                    if (c.NextOffset <= offset)
                        throw new IOException($"업로드가 진행되지 않음 (offset {offset}): {file.Name}");
                    offset = c.NextOffset;
                    break;
                case DriveClient.Progress.Done d:
                    result = d.Uploaded;
                    break;
            }
        }

        var uploaded = result.Value;
        var sizeOk = uploaded.Size == null || uploaded.Size == total;
        var md5Ok = uploaded.Md5 == null
                    || string.Equals(uploaded.Md5, Storage.Md5Hex(file.FullName), StringComparison.OrdinalIgnoreCase);
        if (!sizeOk || !md5Ok)
        {
            // 이 세션은 못 쓰니 버리고 다음 실행에서 새로 올린다.
            Prefs.SetUploadSession(file.Name, null);
            throw new IOException(sizeOk
                ? "업로드 MD5 불일치: " + file.Name
                : $"업로드 크기 불일치 ({uploaded.Size} != {total}): {file.Name}");
        }

        // 수집 기록. 기록 파일 자신은 남기지 않는다 (기록의 기록이 꼬리를 문다).
        if (folderKey != "index")
            IndexLog.Record(file.Name, folderKey, total, uploaded.Id, uploaded.Md5);

        // 로컬 삭제 → 세션 정리 순서. 사이에 죽어도 다음 실행의 세션 정리에서 고아 세션이 지워진다.
        try
        {
            file.Delete();
        }
        catch (IOException e)
        {
            Log.Warn($"로컬 삭제 실패 {file.Name}: {e.Message}");
        }
        Prefs.SetUploadSession(file.Name, null);
        Log.Info($"올림 {file.Name} → {uploaded.Id}");
        return true;
    }
}
