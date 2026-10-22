using System.Runtime.InteropServices;

namespace Handshake.Engine.Client;

/// <summary>Bare Win32 window for the test client (no UI framework: nothing between us and DXGI).</summary>
public sealed class Win32Window : IDisposable
{
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] static extern uint MsgWaitForMultipleObjects(uint n, IntPtr handles, bool all, uint ms, uint mask);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool AdjustWindowRectEx(ref RECT r, uint style, bool menu, uint exStyle);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint action, uint p, out RECT r, uint win);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowTextW(IntPtr hwnd, string text);
    [DllImport("user32.dll")] static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(IntPtr name);

    const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000;
    public const uint WM_DESTROY = 0x2, WM_SIZE = 0x5, WM_CLOSE = 0x10, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201,
        WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MOUSEWHEEL = 0x20A, WM_KEYDOWN = 0x100;

    readonly WndProc proc; // keep the delegate alive
    public IntPtr Handle { get; }
    public bool Closed { get; private set; }
    public event Action<uint, IntPtr, IntPtr>? Message;

    public Win32Window(string title, int clientW, int clientH)
    {
        proc = Proc;
        var inst = GetModuleHandleW(IntPtr.Zero);
        var cls = "HandshakeClient";
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0x3,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = inst,
            hCursor = LoadCursorW(IntPtr.Zero, 32512),
            lpszClassName = cls,
        };
        RegisterClassExW(ref wc);

        // Fit inside 85% of the work area, keeping the stream's aspect ratio
        SystemParametersInfoW(0x30, 0, out var work, 0);
        double maxW = (work.Right - work.Left) * 0.85, maxH = (work.Bottom - work.Top) * 0.85;
        double s = Math.Min(1, Math.Min(maxW / clientW, maxH / clientH));
        var r = new RECT { Left = 0, Top = 0, Right = (int)(clientW * s), Bottom = (int)(clientH * s) };
        AdjustWindowRectEx(ref r, WS_OVERLAPPEDWINDOW, false, 0);
        Handle = CreateWindowExW(0, cls, title, WS_OVERLAPPEDWINDOW | WS_VISIBLE, 60, 60, r.Right - r.Left, r.Bottom - r.Top, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        ShowWindow(Handle, 5);
    }

    IntPtr Proc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        Message?.Invoke(msg, w, l);
        if (msg == WM_CLOSE || msg == WM_DESTROY) Closed = true;
        return DefWindowProcW(hwnd, msg, w, l);
    }

    public (int w, int h) ClientSize
    {
        get
        {
            GetClientRect(Handle, out var r);
            return (Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
        }
    }

    public void SetTitle(string text) => SetWindowTextW(Handle, text);

    /// <summary>Pumps pending messages; waits up to timeoutMs for new ones.</summary>
    public void Pump(uint timeoutMs)
    {
        MsgWaitForMultipleObjects(0, IntPtr.Zero, false, timeoutMs, 0x04FF);
        while (PeekMessageW(out var m, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref m);
            DispatchMessageW(ref m);
        }
    }

    public void Dispose()
    {
        if (!Closed) DestroyWindow(Handle);
    }
}
