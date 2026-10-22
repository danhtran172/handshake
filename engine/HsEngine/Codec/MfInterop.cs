using System.Runtime.InteropServices;
using SharpGen.Runtime;

namespace Handshake.Engine.Codecs;

/// <summary>Media Foundation GUIDs used by the encoder and decoder (from mfapi.h / codecapi.h).</summary>
public static class MfGuids
{
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid Hevc = new("43564548-0000-0010-8000-00AA00389B71");
    public static readonly Guid Av1 = new("31305641-0000-0010-8000-00AA00389B71");
    public static readonly Guid Nv12 = new("3231564E-0000-0010-8000-00AA00389B71");

    public static readonly Guid CategoryVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid CategoryVideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");

    public static readonly Guid MT_MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MT_Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MT_FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MT_FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MT_AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MT_InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MT_PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MT_Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MT_SequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    public static readonly Guid MT_NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    public static readonly Guid MT_YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");

    public static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    public static readonly Guid TransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid D3D11Aware = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");
    public static readonly Guid HardwareVendorId = new("3aecb0cc-035b-4bcc-8185-2b8d551ef3af");
    public static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid SampleCleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // codecapi.h
    public static readonly Guid CodecLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid CodecRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CodecMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid CodecMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid CodecBufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    public static readonly Guid CodecGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid CodecBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid CodecQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid CodecForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    public const int METransformNeedInput = 601;
    public const int METransformHaveOutput = 602;
    public const int METransformDrainComplete = 603;

    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);

    public static ulong Pack(uint hi, uint lo) => ((ulong)hi << 32) | lo;

    /// <summary>Reads a UINT32 attribute, 0 when absent (Vortice throws on missing keys).</summary>
    public static uint GetOrZero(Vortice.MediaFoundation.IMFAttributes attrs, Guid key) =>
        attrs.GetUInt32(key, out var v).Success ? v : 0;
}

/// <summary>
/// Minimal ICodecAPI (codecapi.h) called through its vtable: Vortice does not wrap it, and it is
/// how hardware encoders take low-latency, rate-control, GOP and "force keyframe" settings.
/// </summary>
public sealed unsafe class CodecApi : IDisposable
{
    static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    const ushort VT_BOOL = 11, VT_UI4 = 19, VT_UI8 = 21;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct Variant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint ui4;
        [FieldOffset(8)] public ulong ui8;
        [FieldOffset(8)] public short boolVal;
    }

    IntPtr ptr;

    CodecApi(IntPtr p) => ptr = p;

    public static CodecApi? From(ComObject obj)
    {
        var iid = IID_ICodecAPI;
        return Marshal.QueryInterface(obj.NativePointer, in iid, out var p) == 0 ? new CodecApi(p) : null;
    }

    int Set(Guid key, Variant value)
    {
        var vtbl = *(IntPtr**)ptr;
        // IUnknown (3) + IsSupported, IsModifiable, GetParameterRange, GetParameterValues, GetDefaultValue, GetValue -> SetValue = slot 9
        var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)vtbl[9];
        return setValue(ptr, &key, &value);
    }

    bool Supported(Guid key)
    {
        var vtbl = *(IntPtr**)ptr;
        var isSupported = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)vtbl[3];
        return isSupported(ptr, &key) == 0;
    }

    /// <summary>Returns true when the encoder accepted the value.</summary>
    public bool SetUInt32(Guid key, uint value) => Supported(key) && Set(key, new Variant { vt = VT_UI4, ui4 = value }) >= 0;

    public bool SetUInt64(Guid key, ulong value) => Supported(key) && Set(key, new Variant { vt = VT_UI8, ui8 = value }) >= 0;

    public bool SetBool(Guid key, bool value) => Supported(key) && Set(key, new Variant { vt = VT_BOOL, boolVal = (short)(value ? -1 : 0) }) >= 0;

    public void Dispose()
    {
        if (ptr != IntPtr.Zero) Marshal.Release(ptr);
        ptr = IntPtr.Zero;
    }
}
