using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Handshake.Engine.Common;

/// <summary>
/// Microsecond clock backed by QueryPerformanceCounter. QPC is system-wide on Windows, so host
/// and client running on the same machine share the same timeline (exact latency measurement).
/// </summary>
public static class Clock
{
    static readonly double TicksToUs = 1_000_000.0 / Stopwatch.Frequency;

    public static long NowUs => (long)(Stopwatch.GetTimestamp() * TicksToUs);

    public static long QpcToUs(long qpc) => (long)(qpc * TicksToUs);
}

/// <summary>
/// Paces a loop at a fixed rate using a high-resolution waitable timer (1 ms Sleep is too coarse
/// for 120 fps). Keeps a fixed schedule, so jitter does not accumulate.
/// </summary>
public sealed class FramePacer : IDisposable
{
    const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    const uint TIMER_ALL_ACCESS = 0x1F0003;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    readonly IntPtr timer;
    readonly long intervalUs;
    long nextUs;

    public FramePacer(double fps)
    {
        intervalUs = (long)(1_000_000 / fps);
        timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        nextUs = Clock.NowUs + intervalUs;
    }

    public long IntervalUs => intervalUs;

    /// <summary>Blocks until the next frame slot. Returns false when we are already late (frame skipped).</summary>
    public bool Wait()
    {
        long now = Clock.NowUs;
        long remaining = nextUs - now;
        if (remaining < -intervalUs)
        {
            // Fell behind by more than a frame: resynchronise instead of bursting
            nextUs = now + intervalUs;
            return false;
        }
        if (remaining > 600)
        {
            if (timer != IntPtr.Zero)
            {
                long due = -(remaining - 400) * 10; // 100 ns units, relative
                SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                WaitForSingleObject(timer, 0xFFFFFFFF);
            }
            else
            {
                Thread.Sleep((int)((remaining - 400) / 1000));
            }
        }
        while (Clock.NowUs < nextUs) Thread.SpinWait(20);
        nextUs += intervalUs;
        return true;
    }

    public void Dispose()
    {
        if (timer != IntPtr.Zero) CloseHandle(timer);
    }
}
