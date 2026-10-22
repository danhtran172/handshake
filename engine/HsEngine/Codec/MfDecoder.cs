using Handshake.Engine.Gpu;
using Handshake.Engine.Net;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Handshake.Engine.Codecs;

public readonly record struct DecodedFrame(ID3D11Texture2D Texture, uint ArraySlice, long SampleTime, IMFSample Sample);

/// <summary>
/// Microsoft H.264 / HEVC decoder MFT with DXVA (GPU decode into D3D11 textures) in low-latency
/// mode: one access unit in, one picture out, no reorder buffering.
/// </summary>
public sealed class MfDecoder : IDisposable
{
    readonly IMFTransform transform;
    readonly Guid subtype;
    readonly bool providesSamples;
    readonly uint width, height, fps;

    public string Name { get; }

    public MfDecoder(GpuDevice gpu, Codec codec, uint width, uint height, uint fps)
    {
        this.width = width;
        this.height = height;
        this.fps = fps;
        subtype = codec == Codec.Hevc ? MfGuids.Hevc : codec == Codec.Av1 ? MfGuids.Av1 : MfGuids.H264;
        var info = new RegisterTypeInfo { GuidMajorType = MfGuids.MediaTypeVideo, GuidSubtype = subtype };
        // SYNCMFT | HARDWARE | SORTANDFILTER: the Microsoft decoders are sync MFTs that use DXVA when given a device
        using var list = MediaFactory.MFTEnumEx(MfGuids.CategoryVideoDecoder, 0x1 | 0x4 | 0x40, info, null);
        IMFActivate? activate = null;
        foreach (var a in list)
        {
            activate = a;
            break;
        }
        if (activate == null) throw new InvalidOperationException($"Không có bộ giải mã {codec}");
        Name = activate.GetString(MfGuids.FriendlyName);
        transform = activate.ActivateObject<IMFTransform>();

        var attrs = transform.Attributes;
        attrs.Set(MfGuids.LowLatency, 1u);
        if (MfGuids.GetOrZero(attrs, MfGuids.TransformAsync) != 0) attrs.Set(MfGuids.TransformAsyncUnlock, 1u);
        transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)gpu.Manager.NativePointer);
        using (var api = CodecApi.From(transform)) api?.SetBool(MfGuids.CodecLowLatencyMode, true);

        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MfGuids.MT_MajorType, MfGuids.MediaTypeVideo);
            inType.Set(MfGuids.MT_Subtype, subtype);
            inType.Set(MfGuids.MT_FrameSize, MfGuids.Pack(width, height));
            inType.Set(MfGuids.MT_FrameRate, MfGuids.Pack(fps, 1));
            inType.Set(MfGuids.MT_InterlaceMode, 2u);
            transform.SetInputType(0, inType, 0);
        }
        SelectNv12Output();
        providesSamples = ((int)transform.GetOutputStreamInfo(0).Flags & 0x100) != 0;
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    void SelectNv12Output()
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = transform.GetOutputAvailableType(0, i); }
            catch { throw new InvalidOperationException("Bộ giải mã không xuất được NV12"); }
            using (t)
            {
                if (t.GetGUID(MfGuids.MT_Subtype) == MfGuids.Nv12)
                {
                    transform.SetOutputType(0, t, 0);
                    return;
                }
            }
        }
    }

    /// <summary>Feeds one Annex-B access unit and returns the pictures that came out (0 or 1 in low-latency mode).</summary>
    public List<DecodedFrame> Decode(ReadOnlySpan<byte> accessUnit, long sampleTime)
    {
        using (var buffer = MediaFactory.MFCreateMemoryBuffer(accessUnit.Length))
        {
            buffer.Lock(out var ptr, out _, out _);
            unsafe { accessUnit.CopyTo(new Span<byte>((void*)ptr, accessUnit.Length)); }
            buffer.Unlock();
            buffer.CurrentLength = accessUnit.Length;
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = sampleTime;
            sample.SampleDuration = 10_000_000L / fps;
            transform.ProcessInput(0, sample, 0);
        }

        var result = new List<DecodedFrame>(1);
        while (true)
        {
            var buf = new OutputDataBuffer { StreamID = 0, Sample = providesSamples ? null! : AllocateSample() };
            var hr = transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buf, out _);
            buf.Events?.Dispose();
            if (hr.Code == MfGuids.MF_E_TRANSFORM_NEED_MORE_INPUT) break;
            if (hr.Code == MfGuids.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                SelectNv12Output();
                continue;
            }
            hr.CheckError();
            var sample = buf.Sample;
            using var media = sample.GetBufferByIndex(0);
            using var dxgi = media.QueryInterface<IMFDXGIBuffer>();
            var texPtr = dxgi.GetResource(MfGuids.IID_ID3D11Texture2D);
            result.Add(new DecodedFrame(new ID3D11Texture2D(texPtr), dxgi.SubresourceIndex, sample.SampleTime, sample));
        }
        return result;
    }

    IMFSample AllocateSample()
    {
        var s = MediaFactory.MFCreateSample();
        s.AddBuffer(MediaFactory.MFCreateMemoryBuffer((int)(width * height * 3 / 2)));
        return s;
    }

    public void Dispose()
    {
        try { transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        transform.Dispose();
    }
}
