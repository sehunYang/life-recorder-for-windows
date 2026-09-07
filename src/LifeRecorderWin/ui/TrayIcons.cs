using System.Drawing;
using System.Drawing.Drawing2D;

namespace LifeRecorderWin.Ui;

/// <summary>
/// 트레이 아이콘을 그때그때 그린다. 색만 다른 점 하나라 이미지 파일을 저장소에 넣을 이유가 없다.
///
///   빨강 = 기록 중, 노랑 = 잠금·화면 꺼짐으로 쉬는 중, 회색 = 꺼짐
/// </summary>
internal static class TrayIcons
{
    private static readonly Dictionary<string, Icon> Cache = new();

    public static Icon For(Status s)
    {
        if (!s.RecordingEnabled) return Get("off", Color.FromArgb(128, 128, 128));
        if (s.ScreenPausedReason != null || !s.ScreenRecording) return Get("paused", Color.FromArgb(230, 160, 30));
        return Get("recording", Color.FromArgb(215, 50, 50));
    }

    private static Icon Get(string key, Color color)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var icon = Draw(color);
            Cache[key] = icon;
            return icon;
        }
    }

    private static Icon Draw(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 4, 4, 24, 24);
            using var ring = new Pen(Color.FromArgb(90, 255, 255, 255), 2f);
            g.DrawEllipse(ring, 4, 4, 24, 24);
        }
        // HICON 은 세 개(꺼짐·쉼·기록 중)만 만들어 캐시에 들고 있는다.
        return Icon.FromHandle(bmp.GetHicon());
    }
}
