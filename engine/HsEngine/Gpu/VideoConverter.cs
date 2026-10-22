using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Handshake.Engine.Gpu;

/// <summary>
/// Fixed-function GPU colour conversion + scaling (ID3D11VideoProcessor):
/// BGRA desktop -> NV12 for the encoder, NV12 decoder output -> BGRA swap chain on the client.
/// Views are cached per (texture, array slice) because decoders hand out texture-array slices.
/// </summary>
public sealed class VideoConverter : IDisposable
{
    readonly ID3D11VideoDevice videoDevice;
    readonly ID3D11VideoContext videoContext;
    readonly ID3D11VideoProcessorEnumerator enumerator;
    readonly ID3D11VideoProcessor processor;
    readonly Dictionary<(IntPtr, uint), ID3D11VideoProcessorInputView> inputViews = new();
    readonly Dictionary<IntPtr, ID3D11VideoProcessorOutputView> outputViews = new();

    public uint InputWidth { get; }
    public uint InputHeight { get; }
    public uint OutputWidth { get; }
    public uint OutputHeight { get; }

    public VideoConverter(GpuDevice gpu, uint inW, uint inH, uint outW, uint outH, uint fps, bool inputIsYuv)
    {
        InputWidth = inW;
        InputHeight = inH;
        OutputWidth = outW;
        OutputHeight = outH;
        videoDevice = gpu.Device.QueryInterface<ID3D11VideoDevice>();
        videoContext = gpu.Context.QueryInterface<ID3D11VideoContext>();
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(fps, 1),
            InputWidth = inW,
            InputHeight = inH,
            OutputFrameRate = new Rational(fps, 1),
            OutputWidth = outW,
            OutputHeight = outH,
            Usage = VideoUsage.OptimalSpeed,
        };
        videoDevice.CreateVideoProcessorEnumerator(content, out enumerator).CheckError();
        processor = videoDevice.CreateVideoProcessor(enumerator, 0);

        // RGB is full range; YUV is BT.709 studio range (what H.264/HEVC decoders on iOS expect)
        var rgb = new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, Nominal_Range = 2 };
        var yuv = new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, Nominal_Range = 1 };
        videoContext.VideoProcessorSetStreamColorSpace(processor, 0, inputIsYuv ? yuv : rgb);
        videoContext.VideoProcessorSetOutputColorSpace(processor, inputIsYuv ? rgb : yuv);
        videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
        videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
    }

    /// <summary>Optional letterboxing: where the picture goes inside the output surface.</summary>
    public void SetDestination(RawRect? rect)
    {
        videoContext.VideoProcessorSetStreamDestRect(processor, 0, rect.HasValue, rect);
    }

    public void SetSource(RawRect? rect)
    {
        videoContext.VideoProcessorSetStreamSourceRect(processor, 0, rect.HasValue, rect);
    }

    public void Convert(ID3D11Texture2D input, uint arraySlice, ID3D11Texture2D output)
    {
        var key = (input.NativePointer, arraySlice);
        if (!inputViews.TryGetValue(key, out var inView))
        {
            inView = videoDevice.CreateVideoProcessorInputView(input, enumerator, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = arraySlice },
            });
            inputViews[key] = inView;
        }
        if (!outputViews.TryGetValue(output.NativePointer, out var outView))
        {
            outView = videoDevice.CreateVideoProcessorOutputView(output, enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });
            outputViews[output.NativePointer] = outView;
        }
        var stream = new VideoProcessorStream { Enable = true, InputSurface = inView };
        videoContext.VideoProcessorBlt(processor, outView, 0, 1, new[] { stream }).CheckError();
    }

    /// <summary>Drop cached views (e.g. after swap-chain resize or decoder reallocation).</summary>
    public void ResetViews()
    {
        foreach (var v in inputViews.Values) v.Dispose();
        foreach (var v in outputViews.Values) v.Dispose();
        inputViews.Clear();
        outputViews.Clear();
    }

    public void Dispose()
    {
        ResetViews();
        processor.Dispose();
        enumerator.Dispose();
        videoContext.Dispose();
        videoDevice.Dispose();
    }
}
