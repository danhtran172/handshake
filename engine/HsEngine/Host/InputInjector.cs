using System.Runtime.InteropServices;
using Handshake.Engine.Gpu;
using Handshake.Engine.Net;

namespace Handshake.Engine.Host;

/// <summary>
/// Applies client pointer events to the streamed display. The engine is per-monitor DPI aware,
/// so the DXGI output rectangle is in physical pixels, the same space SetCursorPos uses.
/// </summary>
public static class InputInjector
{
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public MOUSEINPUT mi; } // 40 bytes on x64, matches the native union size

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    const uint LEFTDOWN = 0x2, LEFTUP = 0x4, RIGHTDOWN = 0x8, RIGHTUP = 0x10, MIDDLEDOWN = 0x20, MIDDLEUP = 0x40, WHEEL = 0x800;

    static Vortice.RawRect? rect;
    static int rectOutput = -1;

    public static void Apply(InputEvent e, int outputIndex)
    {
        if (rect == null || rectOutput != outputIndex)
        {
            var o = GpuDevice.ListOutputs().FirstOrDefault(x => x.index == outputIndex);
            rect = o.rect;
            rectOutput = outputIndex;
        }
        var r = rect.Value;
        int x = r.Left + (int)((long)e.X * (r.Right - r.Left - 1) / 65535);
        int y = r.Top + (int)((long)e.Y * (r.Bottom - r.Top - 1) / 65535);
        switch (e.Kind)
        {
            case 1:
                SetCursorPos(x, y);
                Mouse(e.Button == 2 ? RIGHTDOWN : e.Button == 1 ? MIDDLEDOWN : LEFTDOWN, 0);
                break;
            case 2:
                SetCursorPos(x, y);
                break;
            case 3:
                SetCursorPos(x, y);
                Mouse(e.Button == 2 ? RIGHTUP : e.Button == 1 ? MIDDLEUP : LEFTUP, 0);
                break;
            case 4:
                Mouse(WHEEL, e.WheelDelta);
                break;
        }
    }

    static void Mouse(uint flags, int data)
    {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}
