using System.Collections.Concurrent;
using Handshake.Engine.Common;
using Handshake.Engine.Gpu;
using Handshake.Engine.Net;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Handshake.Engine.Codecs;

public sealed record EncodedFrame(uint FrameId, long CaptureUs, long EncodedUs, bool Key, byte[] Data);

/// <summary>
/// Hardware video encoder (AMD AMF / NVIDIA NVENC / Intel QSV through their Media Foundation MFTs),
/// configured for streaming: low-latency mode, no B-frames, CBR with a ~1 frame VBV, long GOP with
/// keyframes only on request. Input is an NV12 texture on the same GPU (zero copy).
/// </summary>
public sealed class MfEncoder : IDisposable
{
    readonly GpuDevice gpu;
    readonly IMFTransform transform;
    readonly IMFMediaEventGenerator events;
    readonly CodecApi? codecApi;
    readonly SemaphoreSlim needInput = new(0);
    readonly ConcurrentDictionary<long, (uint id, long captureUs, long inputUs)> inFlight = new();
    readonly Thread eventThread;
    readonly bool providesSamples;
    readonly int outputSize;
    readonly long frameDuration;
    volatile bool running = true;
    byte[]? sequenceHeader;
    bool forceKey;

    public string Name { get; }
    public Codec Codec { get; }
    public uint Width { get; }
    public uint Height { get; }
    public uint Fps { get; }
    public uint Kbps { get; private set; }
    public event Action<EncodedFrame>? FrameEncoded;
    public List<string> Notes { get; } = new();

    public MfEncoder(GpuDevice gpu, Codec codec, uint width, uint height, uint fps, uint kbps)
    {
        this.gpu = gpu;
        Codec = codec;
        Width = width;
        Height = height;
        Fps = fps;
        Kbps = kbps;
        frameDuration = 10_000_000L / fps;

        var subtype = codec switch { Codec.Hevc => MfGuids.Hevc, Codec.Av1 => MfGuids.Av1, _ => MfGuids.H264 };
        var activate = FindEncoder(subtype, gpu.VendorId) ?? throw new InvalidOperationException($"Không có bộ mã hoá phần cứng {codec} trên {gpu.Name}");
        Name = activate.GetString(MfGuids.FriendlyName);
        transform = activate.ActivateObject<IMFTransform>();

        var attrs = transform.Attributes;
        if (MfGuids.GetOrZero(attrs, MfGuids.TransformAsync) != 0) attrs.Set(MfGuids.TransformAsyncUnlock, 1u);
        attrs.Set(MfGuids.LowLatency, 1u);
        transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)gpu.Manager.NativePointer);

        codecApi = CodecApi.From(transform);
        ApplyCodecSettings(beforeTypes: true);

        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MfGuids.MT_MajorType, MfGuids.MediaTypeVideo);
            outType.Set(MfGuids.MT_Subtype, subtype);
            outType.Set(MfGuids.MT_FrameSize, MfGuids.Pack(width, height));
            outType.Set(MfGuids.MT_FrameRate, MfGuids.Pack(fps, 1));
            outType.Set(MfGuids.MT_PixelAspectRatio, MfGuids.Pack(1, 1));
            outType.Set(MfGuids.MT_AvgBitrate, kbps * 1000);
            outType.Set(MfGuids.MT_InterlaceMode, 2u); // progressive
            if (codec == Codec.H264) outType.Set(MfGuids.MT_Mpeg2Profile, 100u); // High, no B-frames
            if (codec == Codec.Hevc) outType.Set(MfGuids.MT_Mpeg2Profile, 1u); // Main
            transform.SetOutputType(0, outType, 0);
        }
        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MfGuids.MT_MajorType, MfGuids.MediaTypeVideo);
            inType.Set(MfGuids.MT_Subtype, MfGuids.Nv12);
            inType.Set(MfGuids.MT_FrameSize, MfGuids.Pack(width, height));
            inType.Set(MfGuids.MT_FrameRate, MfGuids.Pack(fps, 1));
            inType.Set(MfGuids.MT_PixelAspectRatio, MfGuids.Pack(1, 1));
            inType.Set(MfGuids.MT_InterlaceMode, 2u);
            inType.Set(MfGuids.MT_NominalRange, 2u); // 16-235
            inType.Set(MfGuids.MT_YuvMatrix, 1u);    // BT.709
            transform.SetInputType(0, inType, 0);
        }
        ApplyCodecSettings(beforeTypes: false);

        try
        {
            using var cur = transform.GetOutputCurrentType(0);
            var blob = cur.GetBlob(MfGuids.MT_SequenceHeader);
            if (blob.Length > 0) sequenceHeader = blob;
        }
        catch { }

        var info = transform.GetOutputStreamInfo(0);
        providesSamples = ((int)info.Flags & 0x100) != 0; // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES
        outputSize = Math.Max(info.Size, (int)(width * height));

        events = transform.QueryInterface<IMFMediaEventGenerator>();
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        eventThread = new Thread(EventLoop) { IsBackground = true, Name = "hs-encoder-events", Priority = ThreadPriority.Highest };
        eventThread.Start();
    }

    static IMFActivate? FindEncoder(Guid subtype, uint vendorId)
    {
        var info = new RegisterTypeInfo { GuidMajorType = MfGuids.MediaTypeVideo, GuidSubtype = subtype };
        using var list = MediaFactory.MFTEnumEx(MfGuids.CategoryVideoEncoder, 0x4 | 0x40, null, info); // HARDWARE | SORTANDFILTER
        IMFActivate? first = null;
        var want = $"VEN_{vendorId:X4}";
        foreach (var a in list)
        {
            string vendor = "";
            try { vendor = a.GetString(MfGuids.HardwareVendorId); } catch { }
            // QueryInterface takes our own reference: the collection releases its items on dispose
            if (vendor.Equals(want, StringComparison.OrdinalIgnoreCase)) return a.QueryInterface<IMFActivate>();
            first ??= a;
        }
        return first?.QueryInterface<IMFActivate>();
    }

    void ApplyCodecSettings(bool beforeTypes)
    {
        if (codecApi == null)
        {
            if (!beforeTypes) Notes.Add("ICodecAPI không có: dùng mặc định của encoder");
            return;
        }
        uint bps = Kbps * 1000;
        void Try(string name, bool ok)
        {
            if (!beforeTypes) Notes.Add($"{name}={(ok ? "ok" : "bỏ qua")}");
        }
        Try("lowLatency", codecApi.SetBool(MfGuids.CodecLowLatencyMode, true));
        Try("rateControl=CBR", codecApi.SetUInt32(MfGuids.CodecRateControlMode, 0));
        Try("bitrate", codecApi.SetUInt32(MfGuids.CodecMeanBitRate, bps));
        Try("maxBitrate", codecApi.SetUInt32(MfGuids.CodecMaxBitRate, bps));
        // VBV of ~1.5 frames: a frame can't exceed this, so it never stalls the link for long
        Try("vbv", codecApi.SetUInt32(MfGuids.CodecBufferSize, (uint)(bps / Fps * 1.5)));
        Try("gop", codecApi.SetUInt32(MfGuids.CodecGopSize, Fps * 10));
        Try("bFrames=0", codecApi.SetUInt32(MfGuids.CodecBPictureCount, 0));
        Try("speed", codecApi.SetUInt32(MfGuids.CodecQualityVsSpeed, 0));
    }

    public void RequestKeyFrame() => forceKey = true;

    public void SetBitrate(uint kbps)
    {
        Kbps = kbps;
        codecApi?.SetUInt32(MfGuids.CodecMeanBitRate, kbps * 1000);
        codecApi?.SetUInt32(MfGuids.CodecMaxBitRate, kbps * 1000);
    }

    /// <summary>
    /// Submits one NV12 frame. Returns false when the encoder is still busy (frame dropped,
    /// so we never queue up latency inside the encoder).
    /// </summary>
    public bool Encode(ID3D11Texture2D nv12, uint frameId, long captureUs)
    {
        if (!needInput.Wait(TimeSpan.FromMilliseconds(1000.0 / Fps))) return false;
        if (forceKey && codecApi != null)
        {
            codecApi.SetUInt32(MfGuids.CodecForceKeyFrame, 1);
            forceKey = false;
        }
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(MfGuids.IID_ID3D11Texture2D, nv12, 0, false);
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        long time = frameId * frameDuration;
        sample.SampleTime = time;
        sample.SampleDuration = frameDuration;
        inFlight[time] = (frameId, captureUs, Clock.NowUs);
        transform.ProcessInput(0, sample, 0);
        return true;
    }

    void EventLoop()
    {
        try
        {
            while (running)
            {
                using var ev = events.GetEvent(0);
                switch ((int)ev.EventType)
                {
                    case MfGuids.METransformNeedInput:
                        needInput.Release();
                        break;
                    case MfGuids.METransformHaveOutput:
                        DrainOutput();
                        break;
                }
            }
        }
        catch (Exception ex) when (!running)
        {
            _ = ex;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[encoder] {ex.Message}");
        }
    }

    void DrainOutput()
    {
        IMFSample? own = null;
        if (!providesSamples)
        {
            own = MediaFactory.MFCreateSample();
            own.AddBuffer(MediaFactory.MFCreateMemoryBuffer(outputSize));
        }
        var buf = new OutputDataBuffer { StreamID = 0, Sample = own! };
        var hr = transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buf, out _);
        if (hr.Code == MfGuids.MF_E_TRANSFORM_STREAM_CHANGE)
        {
            using var t = transform.GetOutputAvailableType(0, 0);
            transform.SetOutputType(0, t, 0);
            return;
        }
        if (hr.Failure) return;
        var sample = buf.Sample;
        try
        {
            long time = sample.SampleTime;
            bool key = false;
            try { key = sample.GetUInt32(MfGuids.SampleCleanPoint) != 0; } catch { }
            using var media = sample.ConvertToContiguousBuffer();
            media.Lock(out var ptr, out _, out var len);
            byte[] data;
            bool addHeader = key && sequenceHeader != null && !StartsWithParameterSets(ptr, len);
            int off = addHeader ? sequenceHeader!.Length : 0;
            data = new byte[off + len];
            if (addHeader) sequenceHeader!.CopyTo(data, 0);
            System.Runtime.InteropServices.Marshal.Copy(ptr, data, off, len);
            media.Unlock();

            inFlight.TryRemove(time, out var meta);
            FrameEncoded?.Invoke(new EncodedFrame(meta.id, meta.captureUs, Clock.NowUs, key, data));
        }
        finally
        {
            sample.Dispose();
            buf.Events?.Dispose();
        }
    }

    unsafe bool StartsWithParameterSets(IntPtr ptr, int len)
    {
        var p = (byte*)ptr;
        for (int i = 0; i + 4 < Math.Min(len, 64); i++)
        {
            if (p[i] == 0 && p[i + 1] == 0 && p[i + 2] == 1)
            {
                int nal = Codec == Codec.Hevc ? (p[i + 3] >> 1) & 0x3F : p[i + 3] & 0x1F;
                return Codec == Codec.Hevc ? nal == 32 || nal == 33 : nal == 7;
            }
        }
        return false;
    }

    public void Dispose()
    {
        running = false;
        try { transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        try { transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero); } catch { }
        try { transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        codecApi?.Dispose();
        events.Dispose();
        transform.Dispose();
    }
}
