using System.Diagnostics;
using System.Text.Json;
using Handshake.Engine.Client;
using Handshake.Engine.Codecs;
using Handshake.Engine.Gpu;
using Handshake.Engine.Host;
using Handshake.Engine.Net;
using Vortice.MediaFoundation;

namespace Handshake.Engine;

public static class Program
{
    const string Usage = """
        hs-engine – Handshake native streaming engine

          hs-engine host   [--port 7430] [--source test|desktop] [--output 0] [--size 1920x1080] [--gpu nvidia|amd]
                           [--fps 120] [--kbps 40000] [--codec h264|hevc|av1] [--min-fps 10]
          hs-engine client [--host 127.0.0.1[,192.168.x.x]] [--port 7430] [--drop-primary-at 3] [--seconds 0] [--vsync] [--input] [--headless] [--max 2560x1440]
          hs-engine mirror [--port 7432] [--seconds 0] [--vsync] [--gpu nvidia|amd]   (nhận màn hình iPhone từ app iOS)
          hs-engine bench  [--seconds 10] [--source test|desktop] [--fps 120] [--kbps 40000] [--codec h264] [--size 1920x1080] [--vsync]
          hs-engine animate [--seconds 0] [--size 900x600]
          hs-engine devices
        """;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0)
        {
            Console.WriteLine(Usage);
            return 1;
        }
        var a = Args.Parse(args.Skip(1));
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            switch (args[0])
            {
                case "host":
                {
                    using var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                    StopOnStdinClose(cts);
                    new HostServer(HostOptionsFrom(a)).Run(cts.Token);
                    return 0;
                }
                case "client":
                    return new TestClient(ClientOptionsFrom(a)).Run();
                case "mirror":
                {
                    var o = ClientOptionsFrom(a);
                    o.ListenPort = a.Int("port", 7432);
                    return new TestClient(o).Run();
                }
                case "bench":
                    return Bench(a);
                case "animate":
                    return Animator.Run(a.Double("seconds", 0), a.Size("size")?.Item1 is uint aw and > 0 ? (int)aw : 900, a.Size("size")?.Item2 is uint ah and > 0 ? (int)ah : 600);
                case "devices":
                    Devices();
                    return 0;
                default:
                    Console.WriteLine(Usage);
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Log.Event("fatal", new { message = ex.Message, type = ex.GetType().Name, stack = ex.StackTrace?.Split('\n').Take(4) });
            return 2;
        }
    }

    /// <summary>When launched by the Electron app, exit as soon as the parent closes our stdin.</summary>
    static void StopOnStdinClose(CancellationTokenSource cts)
    {
        if (!Console.IsInputRedirected) return;
        new Thread(() =>
        {
            try { while (Console.In.ReadLine() != null) { } } catch { }
            cts.Cancel();
        }) { IsBackground = true }.Start();
    }

    static HostOptions HostOptionsFrom(Args a)
    {
        var o = new HostOptions
        {
            Port = a.Int("port", 7430),
            Source = a.Str("source", "test"),
            Output = a.Int("output", 0),
            Fps = (uint)a.Int("fps", 120),
            Kbps = (uint)a.Int("kbps", 40_000),
            MinFps = (uint)a.Int("min-fps", 10),
            Gpu = a.Has("gpu") ? a.Str("gpu", "auto") : null,
            Codec = a.Str("codec", "h264") switch { "hevc" or "h265" => Codec.Hevc, "av1" => Codec.Av1, _ => Codec.H264 },
        };
        if (a.Size("size") is var (w, h) && w > 0) { o.Width = w; o.Height = h; }
        return o;
    }

    static ClientOptions ClientOptionsFrom(Args a)
    {
        var o = new ClientOptions
        {
            Hosts = a.Str("host", "127.0.0.1").Split(',', StringSplitOptions.RemoveEmptyEntries),
            DropPrimaryAt = a.Double("drop-primary-at", 0),
            Port = a.Int("port", 7430),
            Seconds = a.Double("seconds", 0),
            VSync = a.Flag("vsync"),
            Input = a.Flag("input"),
            Window = !a.Flag("headless"),
            Gpu = a.Has("gpu") ? a.Str("gpu", "auto") : null,
            Fps = (ushort)a.Int("fps", 120),
        };
        if (a.Size("max") is var (w, h) && w > 0) { o.MaxWidth = (ushort)w; o.MaxHeight = (ushort)h; }
        return o;
    }

    /// <summary>Runs host and client as two processes on this machine and summarises the client's numbers.</summary>
    static int Bench(Args a)
    {
        int port = a.Int("port", 7431);
        double seconds = a.Double("seconds", 10);
        string exe = Environment.ProcessPath!;
        string hostArgs = $"host --port {port} --source {a.Str("source", "test")} --fps {a.Int("fps", 120)} --kbps {a.Int("kbps", 40000)} --codec {a.Str("codec", "h264")}"
            + (a.Has("size") ? $" --size {a.Str("size", "")}" : "") + (a.Has("output") ? $" --output {a.Int("output", 0)}" : "")
            + (a.Has("gpu") ? $" --gpu {a.Str("gpu", "auto")}" : "");
        bool desktop = a.Str("source", "test") == "desktop";
        string clientArgs = $"client --port {port} --seconds {seconds} --fps {a.Int("fps", 120)}" + (a.Flag("vsync") ? " --vsync" : "")
            + (a.Has("host") ? $" --host {a.Str("host", "127.0.0.1")}" : "")
            + (a.Has("drop-primary-at") ? $" --drop-primary-at {a.Double("drop-primary-at", 0)}" : "")
            + (desktop || a.Flag("headless") ? " --headless" : "")
            + (a.Has("client-gpu") ? $" --gpu {a.Str("client-gpu", "auto")}" : "");

        // Desktop benchmark: an animation window gives the screen 1 update per refresh, and the
        // client runs headless so it does not film its own output
        var animLines = new List<string>();
        Process? anim = desktop ? Start(exe, $"animate --seconds {seconds + 4}", animLines) : null;
        if (anim != null) Thread.Sleep(800);

        var hostLines = new List<string>();
        var host = Start(exe, hostArgs, hostLines);
        Thread.Sleep(800);
        var clientLines = new List<string>();
        var client = Start(exe, clientArgs, clientLines);
        client.WaitForExit((int)(seconds * 1000) + 20_000);
        Thread.Sleep(300);
        try { host.StandardInput.Close(); host.WaitForExit(3000); } catch { }
        if (!host.HasExited) host.Kill();
        if (anim != null && !anim.HasExited) anim.Kill();
        var animFps = animLines.Where(l => l.Contains("\"animate\"")).Skip(1).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("fps").GetInt32()).ToList();
        if (animFps.Count > 0) Console.WriteLine($"anim   cửa sổ animation cập nhật màn hình ~{animFps.Average():0} lần/giây");

        foreach (var l in hostLines.Where(l => l.Contains("\"stream\"") || l.Contains("\"error\"") || l.Contains("\"fatal\""))) Console.WriteLine("host   " + l);
        foreach (var l in clientLines.Where(l => l.Contains("client-render") || l.Contains("fatal") || l.Contains("decode-error")
            || l.Contains("path-") || l.Contains("failover") || l.Contains("simulate-unplug"))) Console.WriteLine("client " + l);
        var hostStats = hostLines.Where(l => l.Contains("host-stats")).Skip(1).ToList();
        if (hostStats.Count > 0) Console.WriteLine("host   " + hostStats[hostStats.Count / 2] + "   (giây giữa phiên)");
        var summary = clientLines.LastOrDefault(l => l.Contains("client-summary"));
        if (summary == null)
        {
            Console.WriteLine("Không có kết quả từ client.");
            foreach (var l in clientLines.TakeLast(10)) Console.WriteLine("  " + l);
            return 3;
        }
        Console.WriteLine();
        using var doc = JsonDocument.Parse(summary);
        var r = doc.RootElement;
        string V(string k) => r.TryGetProperty(k, out var v) ? v.ToString() : "-";
        Console.WriteLine($"  Khung hình         : {V("frames")} trong {seconds}s  →  {V("fps")} fps");
        Console.WriteLine($"  Độ trễ chụp→hiện   : trung bình {V("latencyAvgMs")} ms · p50 {V("latencyP50Ms")} · p95 {V("latencyP95Ms")} · p99 {V("latencyP99Ms")} · max {V("latencyMaxMs")}");
        Console.WriteLine($"  Trong đó           : mã hoá {V("encodeMs")} ms · mạng {V("networkMs")} ms · giải mã {V("decodeMs")} ms");
        Console.WriteLine($"  Nhịp khung p99     : {V("frameGapP99Ms")} ms (lý tưởng {Math.Round(1000.0 / a.Int("fps", 120), 2)} ms) · khoảng hở lớn nhất {V("frameGapMaxMs")} ms · chuyển đường {V("failovers")} lần");
        Console.WriteLine($"  Băng thông         : {V("mbps")} Mbit/s · bỏ khung {V("skipped")} · lỗi giải mã {V("decodeErrors")}");
        return 0;
    }

    static Process Start(string exe, string args, List<string> sink)
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            },
        };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sink) sink.Add(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sink) sink.Add("stderr: " + e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    static void Devices()
    {
        foreach (var o in GpuDevice.ListOutputs())
            Log.Event("output", new { o.index, o.adapter, o.device, x = o.rect.Left, y = o.rect.Top, width = o.rect.Right - o.rect.Left, height = o.rect.Bottom - o.rect.Top });
        foreach (var (name, guid) in new[] { ("h264", MfGuids.H264), ("hevc", MfGuids.Hevc), ("av1", MfGuids.Av1) })
        {
            var info = new RegisterTypeInfo { GuidMajorType = MfGuids.MediaTypeVideo, GuidSubtype = guid };
            using var list = MediaFactory.MFTEnumEx(MfGuids.CategoryVideoEncoder, 0x4 | 0x40, null, info);
            foreach (var act in list)
            {
                string vendor = "";
                try { vendor = act.GetString(MfGuids.HardwareVendorId); } catch { }
                Log.Event("encoder", new { codec = name, name = act.GetString(MfGuids.FriendlyName), vendor });
            }
        }
    }

    sealed class Args
    {
        readonly Dictionary<string, string> values = new();
        public static Args Parse(IEnumerable<string> args)
        {
            var a = new Args();
            string? key = null;
            foreach (var s in args)
            {
                if (s.StartsWith("--"))
                {
                    if (key != null) a.values[key] = "true";
                    key = s[2..];
                    var eq = key.IndexOf('=');
                    if (eq > 0) { a.values[key[..eq]] = key[(eq + 1)..]; key = null; }
                }
                else if (key != null) { a.values[key] = s; key = null; }
            }
            if (key != null) a.values[key] = "true";
            return a;
        }
        public bool Has(string k) => values.ContainsKey(k);
        public bool Flag(string k) => values.TryGetValue(k, out var v) && v != "false";
        public string Str(string k, string d) => values.TryGetValue(k, out var v) ? v : d;
        public int Int(string k, int d) => values.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : d;
        public double Double(string k, double d) => values.TryGetValue(k, out var v) && double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : d;
        public (uint, uint)? Size(string k)
        {
            if (!values.TryGetValue(k, out var v)) return null;
            var p = v.ToLowerInvariant().Split('x');
            return p.Length == 2 && uint.TryParse(p[0], out var w) && uint.TryParse(p[1], out var h) ? (w, h) : null;
        }
    }
}
