using System.Drawing;
using System.Runtime.InteropServices;

namespace LifeRecorderWin.Capture;

/// <summary>
/// 가상 데스크톱 한 장을 직접 떠서, 가릴 창(<see cref="PrivateWindows"/>)을 검게 칠하고
/// 커서를 얹은 뒤 raw 프레임(bgr0)으로 돌려준다. ffmpeg 은 이것을 stdin 으로 받아 인코딩만 한다.
///
/// 예전에는 ffmpeg 의 gdigrab 이 화면을 떴다. 그러면 프레임이 파일에 들어가기 전에 손댈 곳이 없어서
/// 창 하나만 가릴 수가 없었다. 뜨는 방식은 gdigrab 과 같다 (화면 DC → BitBlt, CAPTUREBLT).
///
/// 뜨기 **직전과 직후에** 가릴 영역을 한 번씩 구해 합친다. 창이 움직이는 중이면 직전 장의 위치까지 이은
/// 사각형을 넉넉히 칠한다 — 대각선으로 끌 때의 모서리와, 화면에 보이는 것이 창 위치보다 한 박자 늦는 몫을 덮는다.
/// 가릴 영역을 구하다 실패하면 프레임 전체를 검게 한다. 모르면 가린다.
/// </summary>
internal sealed class ScreenGrabber : IDisposable
{
    private readonly Rectangle _rect;
    private readonly IntPtr _screenDc;
    private readonly IntPtr _memDc;
    private readonly IntPtr _dib;
    private readonly IntPtr _oldBmp;
    private readonly IntPtr _bits;
    private readonly byte[] _frame;
    private bool _failLogged;
    /// <summary>가린 창의 직전 장 위치. 움직였는지 보려고.</summary>
    private Dictionary<IntPtr, Rectangle> _lastRects = new();

    /// <summary>움직이는 가린 창은 이만큼 더 넓게 칠한다.</summary>
    private const int MovingMargin = 32;

    /// <summary>직전 프레임에서 가린 창의 수. 늘고 줄 때 로그를 남기는 데 쓴다.</summary>
    public int MaskedWindows { get; private set; }

    public int FrameBytes => _frame.Length;

    public ScreenGrabber(Rectangle rect)
    {
        _rect = rect;
        _screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(_screenDc);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = rect.Width,
            biHeight = -rect.Height,   // 음수면 위에서 아래로. ffmpeg rawvideo 의 줄 순서와 같다
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = CreateDIBSection(_screenDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException($"프레임 버퍼를 만들지 못했습니다 ({rect.Width}x{rect.Height})");
        }
        _oldBmp = SelectObject(_memDc, _dib);
        _frame = new byte[rect.Width * rect.Height * 4];
    }

    /// <summary>한 장. 돌려준 배열은 다음 호출 때 덮어쓴다.</summary>
    public byte[] Grab()
    {
        var before = MaskRegion(out var n1);
        var ok = BitBlt(_memDc, 0, 0, _rect.Width, _rect.Height, _screenDc, _rect.Left, _rect.Top, SRCCOPY | CAPTUREBLT);
        var after = MaskRegion(out var n2);

        using (var g = Graphics.FromHdc(_memDc))
        {
            if (!ok || before == null || after == null)
            {
                // 화면을 못 떴거나(보안 데스크톱·UAC) 가릴 곳을 모른다. 통째로 검게.
                g.Clear(Color.Black);
                if (!_failLogged)
                {
                    _failLogged = true;
                    Log.Warn(ok ? "가릴 창을 확인하지 못해 프레임 전체를 검게 칠합니다" : "화면을 뜨지 못해 검은 프레임을 넣습니다");
                }
            }
            else
            {
                _failLogged = false;
                before.Union(after);
                before.Translate(-_rect.Left, -_rect.Top);
                g.FillRegion(Brushes.Black, before);
            }
        }
        before?.Dispose();
        after?.Dispose();

        var masked = Math.Max(n1, n2);
        // 메뉴·미리보기가 뜰 때마다 수가 바뀐다. 가리기 시작·끝만 남긴다.
        if ((masked > 0) != (MaskedWindows > 0))
            Log.Info(masked > 0 ? $"화면 가림: 창 {masked}개를 검게 칠합니다" : "화면 가림: 가릴 창이 없습니다");
        MaskedWindows = masked;

        if (Config.ScreenDrawMouse) DrawCursor();
        GdiFlush();
        Marshal.Copy(_bits, _frame, 0, _frame.Length);
        return _frame;
    }

    /// <summary>
    /// 가릴 창이 **보이는 부분**. 위에서 아래로 훑으며, 위에 덮인 불투명한 창의 몫은 뺀다 —
    /// 시크릿 창 위에 메모장을 띄워 두면 메모장은 그대로 찍힌다. 실패하면 null.
    /// </summary>
    private Region? MaskRegion(out int count)
    {
        count = 0;
        Region? mask = null;
        try
        {
            mask = new Region();
            mask.MakeEmpty();
            using var covered = new Region();
            covered.MakeEmpty();
            var rects = new Dictionary<IntPtr, Rectangle>();
            foreach (var w in PrivateWindows.Snapshot())
            {
                if (w.Masked)
                {
                    var rect = w.Rect;
                    if (w.Handle != IntPtr.Zero)
                    {
                        rects[w.Handle] = rect;
                        if (_lastRects.TryGetValue(w.Handle, out var last) && last != rect)
                        {
                            rect = Rectangle.Union(rect, last);
                            rect.Inflate(MovingMargin, MovingMargin);
                        }
                        count++;
                    }
                    using var r = new Region(rect);
                    r.Exclude(covered);
                    mask.Union(r);
                }
                if (w.Opaque) covered.Union(w.Rect);
            }
            // 직전·직후 두 번 부르므로, 직후 것이 다음 장의 "직전 위치"가 된다.
            _lastRects = rects;
            return mask;
        }
        catch (Exception e)
        {
            mask?.Dispose();
            Log.Warn("가릴 창 확인 실패: " + e.Message);
            return null;
        }
    }

    /// <summary>gdigrab 의 -draw_mouse 1 과 같다. 가린 영역 위에서도 커서는 그린다 — 커서는 내용이 아니다.</summary>
    private void DrawCursor()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
        if (!GetIconInfo(ci.hCursor, out var ii)) return;
        try
        {
            DrawIconEx(_memDc, ci.ptScreenPos.X - ii.xHotspot - _rect.Left, ci.ptScreenPos.Y - ii.yHotspot - _rect.Top,
                ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
    }

    public void Dispose()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBmp != IntPtr.Zero) SelectObject(_memDc, _oldBmp);
            DeleteDC(_memDc);
        }
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const int CURSOR_SHOWING = 1;
    private const int DI_NORMAL = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
}
