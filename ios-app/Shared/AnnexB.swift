import CoreMedia
import Foundation

/// Conversion between the wire format (Annex-B access units, start codes, parameter sets in-band
/// on keyframes) and what VideoToolbox / AVFoundation use (length-prefixed NAL units plus a
/// CMVideoFormatDescription).
enum AnnexB {
    static let startCode: [UInt8] = [0, 0, 0, 1]

    /// Splits an Annex-B access unit into NAL units, without start codes.
    static func nalUnits(_ data: Data) -> [Data] {
        var units: [Data] = []
        data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            let p = raw.bindMemory(to: UInt8.self)
            guard let base = p.baseAddress else { return }
            let n = p.count
            var i = 0
            var start = -1
            func flush(_ end: Int) {
                var e = end
                while e > start && p[e - 1] == 0 { e -= 1 } // zero byte of a 4-byte start code / trailing zeros
                if e > start { units.append(Data(bytes: base + start, count: e - start)) }
            }
            while i + 2 < n {
                if p[i] == 0 && p[i + 1] == 0 && p[i + 2] == 1 {
                    if start >= 0 { flush(i) }
                    i += 3
                    start = i
                } else {
                    i += 1
                }
            }
            if start >= 0 && start < n { flush(n) }
        }
        return units
    }

    static func nalType(_ unit: Data, _ codec: Codec) -> UInt8 {
        guard let first = unit.first else { return 0 }
        return codec == .hevc ? (first >> 1) & 0x3F : first & 0x1F
    }

    static func isParameterSet(_ unit: Data, _ codec: Codec) -> Bool {
        let t = nalType(unit, codec)
        return codec == .hevc ? (32...34).contains(t) : (t == 7 || t == 8)
    }

    /// Builds the decoder format from the SPS/PPS (and VPS for HEVC) found in a keyframe.
    static func formatDescription(codec: Codec, units: [Data]) -> CMVideoFormatDescription? {
        let wanted: [UInt8] = codec == .hevc ? [32, 33, 34] : [7, 8]
        var params: [Data] = []
        for t in wanted {
            guard let u = units.first(where: { nalType($0, codec) == t }) else { return nil }
            params.append(u)
        }
        let buffers = params.map { d -> UnsafeMutablePointer<UInt8> in
            let p = UnsafeMutablePointer<UInt8>.allocate(capacity: d.count)
            d.copyBytes(to: p, count: d.count)
            return p
        }
        defer { buffers.forEach { $0.deallocate() } }
        let pointers = buffers.map { UnsafePointer($0) }
        let sizes = params.map { $0.count }
        var format: CMVideoFormatDescription?
        let status: OSStatus
        if codec == .hevc {
            status = CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                allocator: kCFAllocatorDefault, parameterSetCount: params.count, parameterSetPointers: pointers,
                parameterSetSizes: sizes, nalUnitHeaderLength: 4, extensions: nil, formatDescriptionOut: &format)
        } else {
            status = CMVideoFormatDescriptionCreateFromH264ParameterSets(
                allocator: kCFAllocatorDefault, parameterSetCount: params.count, parameterSetPointers: pointers,
                parameterSetSizes: sizes, nalUnitHeaderLength: 4, formatDescriptionOut: &format)
        }
        return status == noErr ? format : nil
    }

    /// One sample (length-prefixed NAL units) marked "display immediately": the PC already paced it.
    static func sampleBuffer(units: [Data], format: CMVideoFormatDescription, codec: Codec) -> CMSampleBuffer? {
        var avcc = Data()
        for u in units where !isParameterSet(u, codec) {
            var length = UInt32(u.count).bigEndian
            withUnsafeBytes(of: &length) { avcc.append(contentsOf: $0) }
            avcc.append(u)
        }
        guard !avcc.isEmpty else { return nil }

        var block: CMBlockBuffer?
        var status = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: avcc.count, blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil, offsetToData: 0, dataLength: avcc.count, flags: kCMBlockBufferAssureMemoryNowFlag,
            blockBufferOut: &block)
        guard status == kCMBlockBufferNoErr, let block else { return nil }
        status = avcc.withUnsafeBytes { raw in
            CMBlockBufferReplaceDataBytes(with: raw.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: avcc.count)
        }
        guard status == kCMBlockBufferNoErr else { return nil }

        var sample: CMSampleBuffer?
        var size = avcc.count
        status = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault, dataBuffer: block, formatDescription: format, sampleCount: 1,
            sampleTimingEntryCount: 0, sampleTimingArray: nil, sampleSizeEntryCount: 1, sampleSizeArray: &size,
            sampleBufferOut: &sample)
        guard status == noErr, let sample else { return nil }
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: true), CFArrayGetCount(attachments) > 0 {
            let dict = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(dict,
                                 Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                                 Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
        }
        return sample
    }

    /// Encoder output (length-prefixed) to an Annex-B access unit; keyframes carry the parameter sets.
    static func accessUnit(from sample: CMSampleBuffer, codec: Codec, includeParameterSets: Bool) -> Data? {
        guard let block = CMSampleBufferGetDataBuffer(sample) else { return nil }
        let total = CMBlockBufferGetDataLength(block)
        guard total > 0 else { return nil }
        var avcc = Data(count: total)
        let status = avcc.withUnsafeMutableBytes { raw in
            CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: total, destination: raw.baseAddress!)
        }
        guard status == kCMBlockBufferNoErr else { return nil }

        var out = Data(capacity: total + 256)
        if includeParameterSets, let format = CMSampleBufferGetFormatDescription(sample) {
            for ps in parameterSets(format, codec) {
                out.append(contentsOf: startCode)
                out.append(ps)
            }
        }
        avcc.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            let p = raw.bindMemory(to: UInt8.self)
            guard let base = p.baseAddress else { return }
            var i = 0
            while i + 4 <= total {
                let n = Int(p[i]) << 24 | Int(p[i + 1]) << 16 | Int(p[i + 2]) << 8 | Int(p[i + 3])
                i += 4
                guard n > 0, i + n <= total else { break }
                out.append(contentsOf: startCode)
                out.append(base + i, count: n)
                i += n
            }
        }
        return out
    }

    static func parameterSets(_ format: CMFormatDescription, _ codec: Codec) -> [Data] {
        var sets: [Data] = []
        var count = 0
        var index = 0
        repeat {
            var pointer: UnsafePointer<UInt8>?
            var size = 0
            guard parameterSet(format, codec, index, &pointer, &size, &count) == noErr, let pointer else { break }
            sets.append(Data(bytes: pointer, count: size))
            index += 1
        } while index < count
        return sets
    }

    private static func parameterSet(_ format: CMFormatDescription, _ codec: Codec, _ index: Int,
                                     _ pointer: UnsafeMutablePointer<UnsafePointer<UInt8>?>,
                                     _ size: UnsafeMutablePointer<Int>, _ count: UnsafeMutablePointer<Int>) -> OSStatus {
        if codec == .hevc {
            return CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(
                format, parameterSetIndex: index, parameterSetPointerOut: pointer, parameterSetSizeOut: size,
                parameterSetCountOut: count, nalUnitHeaderLengthOut: nil)
        }
        return CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            format, parameterSetIndex: index, parameterSetPointerOut: pointer, parameterSetSizeOut: size,
            parameterSetCountOut: count, nalUnitHeaderLengthOut: nil)
    }
}
