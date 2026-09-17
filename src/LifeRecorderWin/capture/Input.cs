using System.Runtime.InteropServices;

namespace LifeRecorderWin.Capture;

/// <summary>마지막 키보드·마우스 입력이 얼마나 오래됐는지. 앞 창 기록과 유휴 판정이 같이 쓴다.</summary>
internal static class Input
{
    /// <summary>마지막 입력 뒤 지난 시간(ms). 틱 카운트가 넘쳐도 unsigned 뺄셈이라 맞다.</summary>
    public static uint IdleMs()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
