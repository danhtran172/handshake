using System.Runtime.InteropServices;
using Handshake.Engine.Host;

namespace Handshake.Engine.Common;

/// <summary>
/// Advertises a service over mDNS/DNS-SD with the responder built into Windows 10 1809+
/// (dnsapi DnsServiceRegister), so the iOS app finds the PC without typing an address.
/// </summary>
public static class Bonjour
{
    const uint DNS_QUERY_REQUEST_VERSION1 = 1;
    const uint DNS_REQUEST_PENDING = 9506;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void RegisterComplete(uint status, IntPtr context, IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr Callback;
        public IntPtr Context;
        public IntPtr Credentials;
        public int UnicastEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Cancel { public IntPtr Reserved; }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName, IntPtr ip4, IntPtr ip6,
        ushort port, ushort priority, ushort weight, uint propertiesCount, string[] keys, string[] values);

    [DllImport("dnsapi.dll")]
    static extern uint DnsServiceRegister(ref RegisterRequest request, ref Cancel cancel);

    [DllImport("dnsapi.dll")]
    static extern uint DnsServiceDeRegister(ref RegisterRequest request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    static extern void DnsServiceFreeInstance(IntPtr instance);

    // dnsapi calls back on its own thread after we return: the delegate must stay alive
    static readonly RegisterComplete OnComplete = (status, _, instance) =>
    {
        Log.Event("bonjour", new { status = status == 0 ? "registered" : $"error {status}" });
        if (instance != IntPtr.Zero) DnsServiceFreeInstance(instance);
    };

    /// <summary>Registers "&lt;PC name&gt;.&lt;type&gt;.local" on every interface. Returns null if Windows refuses.</summary>
    public static IDisposable? Advertise(string type, int port)
    {
        try
        {
            string host = Environment.MachineName;
            var instance = DnsServiceConstructInstance($"{host}.{type}.local", $"{host}.local", IntPtr.Zero, IntPtr.Zero,
                (ushort)port, 0, 0, 1, new[] { "v" }, new[] { "1" });
            if (instance == IntPtr.Zero) return null;
            var request = new RegisterRequest
            {
                Version = DNS_QUERY_REQUEST_VERSION1,
                ServiceInstance = instance,
                Callback = Marshal.GetFunctionPointerForDelegate(OnComplete),
            };
            var cancel = new Cancel();
            uint r = DnsServiceRegister(ref request, ref cancel);
            if (r != DNS_REQUEST_PENDING)
            {
                DnsServiceFreeInstance(instance);
                Log.Event("bonjour", new { status = $"error {r}", type });
                return null;
            }
            return new Registration(request);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null; // Windows older than 1809
        }
    }

    sealed class Registration(RegisterRequest request) : IDisposable
    {
        RegisterRequest request = request;
        bool done;

        public void Dispose()
        {
            if (done) return;
            done = true;
            // Sends the goodbye packet so iPhones drop the entry right away
            DnsServiceDeRegister(ref request, IntPtr.Zero);
        }
    }
}
