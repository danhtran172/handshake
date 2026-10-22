import CoreMedia
import Foundation
import VideoToolbox

/// Hardware H.264 encoder tuned for latency: low-latency rate control, real time, no B-frames,
/// keyframes only when asked for (start, reconnect, or a KeyRequest after a decode error on the PC).
final class VideoEncoder {
    struct Output {
        let accessUnit: Data
        let key: Bool
        let captureUs: Int64
        let encodeUs: Int64
    }

    let width: Int
    let height: Int
    private var session: VTCompressionSession?
    private var forceKey = true
    private let onOutput: (Output) -> Void

    /// `onOutput` runs on a VideoToolbox thread.
    init?(width: Int, height: Int, fps: Int, kbps: Int, onOutput: @escaping (Output) -> Void) {
        self.width = width
        self.height = height
        self.onOutput = onOutput
        let spec: [CFString: Any] = [kVTVideoEncoderSpecification_EnableLowLatencyRateControl: true]
        var created: VTCompressionSession?
        let status = VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault, width: Int32(width), height: Int32(height), codecType: kCMVideoCodecType_H264,
            encoderSpecification: spec as CFDictionary, imageBufferAttributes: nil, compressedDataAllocator: nil,
            outputCallback: nil, refcon: nil, compressionSessionOut: &created)
        guard status == noErr, let created else { return nil }
        session = created
        set(kVTCompressionPropertyKey_RealTime, kCFBooleanTrue)
        set(kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse)
        set(kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_ConstrainedHigh_AutoLevel)
        set(kVTCompressionPropertyKey_AverageBitRate, NSNumber(value: kbps * 1000))
        set(kVTCompressionPropertyKey_ExpectedFrameRate, NSNumber(value: fps))
        set(kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, NSNumber(value: 10))
        VTCompressionSessionPrepareToEncodeFrames(created)
    }

    deinit { invalidate() }

    private func set(_ key: CFString, _ value: CFTypeRef) {
        guard let session else { return }
        VTSessionSetProperty(session, key: key, value: value) // unsupported keys are ignored
    }

    func requestKeyFrame() { forceKey = true }

    @discardableResult
    func encode(_ pixels: CVPixelBuffer, pts: CMTime, captureUs: Int64) -> Bool {
        guard let session else { return false }
        var props: CFDictionary?
        if forceKey {
            props = [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary
            forceKey = false
        }
        let start = Clock.nowUs
        let status = VTCompressionSessionEncodeFrame(
            session, imageBuffer: pixels, presentationTimeStamp: pts, duration: .invalid,
            frameProperties: props, infoFlagsOut: nil
        ) { [weak self] status, _, sample in
            guard let self, status == noErr, let sample else { return }
            let key = !VideoEncoder.isNotSync(sample)
            guard let au = AnnexB.accessUnit(from: sample, codec: .h264, includeParameterSets: key) else { return }
            self.onOutput(Output(accessUnit: au, key: key, captureUs: captureUs, encodeUs: Clock.nowUs - start))
        }
        return status == noErr
    }

    func invalidate() {
        guard let session else { return }
        VTCompressionSessionInvalidate(session)
        self.session = nil
    }

    private static func isNotSync(_ sample: CMSampleBuffer) -> Bool {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[String: Any]],
              let first = attachments.first else { return false }
        return first[kCMSampleAttachmentKey_NotSync as String] as? Bool ?? false
    }
}
