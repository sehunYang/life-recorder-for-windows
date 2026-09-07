using System.Globalization;

namespace LifeRecorderWin;

/// <summary>
/// 하루 한 개짜리 텍스트 로그. 트레이 앱이라 콘솔이 없어서, 무슨 일이 있었는지는 이 파일이 전부다.
/// <c>%LOCALAPPDATA%\LifeRecorder\logs\</c> 에 쌓이고 2주가 지나면 지운다.
/// </summary>
internal static class Log
{
    private static readonly object Lock = new();
    private static string _day = "";
    private static StreamWriter? _writer;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        var now = DateTime.Now;
        var line = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + level + " " + msg;
        try
        {
            lock (Lock)
            {
                var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (_writer == null || day != _day)
                {
                    _writer?.Dispose();
                    _day = day;
                    var path = Path.Combine(Storage.LogDir, "liferecorder-" + day + ".log");
                    _writer = new StreamWriter(path, append: true) { AutoFlush = true };
                    Prune();
                }
                _writer.WriteLine(line);
            }
        }
        catch (IOException)
        {
            // 로그를 못 쓴다고 녹화를 멈출 이유는 없다.
        }
    }

    private static void Prune()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var f in new DirectoryInfo(Storage.LogDir).GetFiles("liferecorder-*.log"))
                if (f.LastWriteTime < cutoff) f.Delete();
        }
        catch (IOException)
        {
        }
    }
}
