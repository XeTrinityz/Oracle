using System.Runtime.InteropServices;

namespace CeZeY.Oracle;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out Bounds rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref Point point);

    public static bool GameInFront()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>The centre of the game window's drawing area in screen pixels, when the game is the window in front.</summary>
    public static bool GameCentre(out int x, out int y)
    {
        x = 0;
        y = 0;
        nint window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out uint pid);
        if (pid != (uint)Environment.ProcessId || !GetClientRect(window, out Bounds r))
            return false;

        var centre = new Point { X = (r.Right - r.Left) / 2, Y = (r.Bottom - r.Top) / 2 };
        if (!ClientToScreen(window, ref centre))
            return false;

        x = centre.X;
        y = centre.Y;
        return true;
    }

    public static bool CursorAt(out int x, out int y)
    {
        bool ok = GetCursorPos(out Point p);
        x = p.X;
        y = p.Y;
        return ok;
    }

    public static void MoveCursor(int x, int y) => SetCursorPos(x, y);
}
