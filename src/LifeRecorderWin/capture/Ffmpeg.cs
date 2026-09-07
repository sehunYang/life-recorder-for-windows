using System.Reflection;

namespace LifeRecorderWin.Capture;

/// <summary>
/// ffmpeg 을 찾는다. 배포판에서는 **exe 안에 들어 있고**, 처음 실행할 때 한 번 꺼내 쓴다.
///
/// 파일 하나만 받아서 바로 실행할 수 있게 하려는 것이다. 두 파일을 같은 폴더에 두라는
/// 조건은 옮기다가 하나를 빠뜨리기 쉽고, 학교 컴퓨터처럼 손대기 번거로운 곳에서 특히 그렇다.
///
/// 저장소에는 여전히 바이너리를 넣지 않는다 (용량 + GPL 재배포).
/// 개발할 때는 <c>scripts\get-ffmpeg.ps1</c> 이 받아 둔 <c>tools\ffmpeg\</c> 를 그대로 쓰고,
/// 배포용으로 묶을 때만 <c>-p:EmbedFfmpeg=true</c> 로 넣는다.
/// </summary>
internal static class Ffmpeg
{
    /// <summary>csproj 의 <c>LogicalName</c> 과 같아야 한다.</summary>
    private const string ResourceName = "ffmpeg.exe";

    private static readonly object Lock = new();
    private static string? _cached;

    public static string? Find()
    {
        lock (Lock)
        {
            if (_cached != null && File.Exists(_cached)) return _cached;

            // 밖에 놓아둔 것이 있으면 그것을 먼저 쓴다.
            // 개발 중이거나, 하드웨어 인코더가 들어간 다른 빌드로 바꿔 끼울 때를 위한 문이다.
            foreach (var candidate in ExternalCandidates())
            {
                if (!File.Exists(candidate)) continue;
                _cached = candidate;
                return candidate;
            }

            return _cached = Unpack();
        }
    }

    /// <summary>꺼내 놓는 자리. 설정과 같은 곳에 둬서 앱을 지울 때 같이 지워진다.</summary>
    private static string UnpackedPath => Path.Combine(Storage.BaseDir, "ffmpeg", "ffmpeg.exe");

    /// <summary>
    /// exe 안에 들어 있는 ffmpeg 을 꺼낸다. 이미 꺼내 둔 것이 크기까지 같으면 그대로 쓴다.
    /// (앱을 새 판으로 바꾸면 크기가 달라지므로 그때 다시 꺼낸다)
    /// </summary>
    private static string? Unpack()
    {
        Stream? src = null;
        try
        {
            src = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (src == null) return null; // 이 빌드에는 안 들어 있다 (개발 빌드)

            var dest = UnpackedPath;
            if (File.Exists(dest) && new FileInfo(dest).Length == src.Length) return dest;

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // 꺼내다 죽어도 반쪽짜리가 남지 않도록 임시 이름으로 쓰고 마지막에 바꾼다.
            var tmp = dest + Storage.Part;
            using (var out_ = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                src.CopyTo(out_);
            File.Move(tmp, dest, overwrite: true);

            Log.Info($"ffmpeg 을 꺼냈습니다: {dest} ({Storage.FmtBytes(new FileInfo(dest).Length)})");
            return dest;
        }
        catch (Exception e)
        {
            Log.Error("ffmpeg 을 꺼내지 못했습니다: " + e.Message);
            return null;
        }
        finally
        {
            src?.Dispose();
        }
    }

    private static IEnumerable<string> ExternalCandidates()
    {
        // 단일 exe 로 묶으면 AppContext.BaseDirectory 는 임시 풀림 폴더라서
        // "실행 파일 옆"을 보려면 ProcessPath 를 봐야 한다.
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir != null)
        {
            yield return Path.Combine(exeDir, "ffmpeg", "ffmpeg.exe");
            yield return Path.Combine(exeDir, "ffmpeg.exe");
        }

        // 개발 배치: 저장소 어딘가 위쪽의 tools\ffmpeg\.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "tools", "ffmpeg", "ffmpeg.exe");
    }
}
