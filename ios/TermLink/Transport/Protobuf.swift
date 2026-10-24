// SPDX-License-Identifier: GPL-3.0-only
import Foundation

struct ProtoField: Equatable, Sendable {
    let number: Int
    let wire: UInt8
    let integer: UInt64
    let data: Data
}
struct ProtoMessage: Sendable {
    private(set) var fields: [ProtoField] = []
    init() {}
    init(_ data: Data, maximum: Int = 1_048_575) throws {
        guard data.count <= maximum else { throw ClientError.limitExceeded }
        let bytes = Array(data); var at = 0
        func varint() throws -> UInt64 {
            var result: UInt64 = 0
            for shift in stride(from: 0, through: 63, by: 7) {
                guard at < bytes.count else { throw ClientError.protocolViolation("truncated varint") }
                let b = bytes[at]; at += 1
                if shift == 63 && b > 1 { throw ClientError.protocolViolation("varint overflow") }
                result |= UInt64(b & 127) << shift
                if b & 128 == 0 { return result }
            }
            throw ClientError.protocolViolation("varint overflow")
        }
        while at < bytes.count {
            guard fields.count < 32768 else { throw ClientError.limitExceeded }
            let tag = try varint(), number = Int(tag >> 3), wire = UInt8(tag & 7)
            guard number > 0 && number <= 536_870_911 else { throw ClientError.protocolViolation("field number") }
            if wire == 0 { fields.append(.init(number: number, wire: wire, integer: try varint(), data: Data())); continue }
            let size: Int
            if wire == 2 {
                let n = try varint(); guard n <= UInt64(bytes.count - at) else { throw ClientError.protocolViolation("field length") }; size = Int(n)
            } else if wire == 1 { size = 8 }
            else if wire == 5 { size = 4 }
            else { throw ClientError.protocolViolation("unsupported protobuf wire type") }
            guard size <= bytes.count - at else { throw ClientError.protocolViolation("truncated field") }
            fields.append(.init(number: number, wire: wire, integer: 0, data: Data(bytes[at..<at+size]))); at += size
        }
    }
    func values(_ number: Int) -> [ProtoField] { fields.filter { $0.number == number } }
    func bytes(_ number: Int) -> Data { fields.last { $0.number == number && $0.wire == 2 }?.data ?? Data() }
    func uint(_ number: Int) -> UInt64 { fields.last { $0.number == number && $0.wire == 0 }?.integer ?? 0 }
    func text(_ number: Int) throws -> String {
        guard let s = String(data: bytes(number), encoding: .utf8) else { throw ClientError.protocolViolation("UTF-8") }; return s
    }
    mutating func set(_ number: Int, _ value: UInt64) { fields.removeAll { $0.number == number }; if value != 0 { fields.append(.init(number: number, wire: 0, integer: value, data: Data())) } }
    mutating func set(_ number: Int, _ value: Data) { fields.removeAll { $0.number == number }; if !value.isEmpty { fields.append(.init(number: number, wire: 2, integer: 0, data: value)) } }
    mutating func set(_ number: Int, _ value: String) { set(number, Data(value.utf8)) }
    mutating func add(_ number: Int, _ value: Data) { fields.append(.init(number: number, wire: 2, integer: 0, data: value)) }
    func encoded(excluding: Set<Int> = []) -> Data {
        var result = Data()
        func append(_ input: UInt64) { var n = input; while n >= 128 { result.append(UInt8(n & 127) | 128); n >>= 7 }; result.append(UInt8(n)) }
        for f in fields.sorted(by: { $0.number < $1.number }) where !excluding.contains(f.number) {
            append(UInt64(f.number << 3) | UInt64(f.wire))
            if f.wire == 0 { append(f.integer) }
            else { if f.wire == 2 { append(UInt64(f.data.count)) }; result.append(f.data) }
        }
        return result
    }
    func validateSingular(_ numbers: Set<Int>) throws {
        for n in numbers { guard values(n).count <= 1 else { throw ClientError.protocolViolation("duplicate field \(n)") } }
    }
}

enum WireType: UInt64, Sendable {
    case heartbeat = 1, goodbye = 2, upsert = 10, removed = 11, subscribe = 12, unsubscribe = 13
    case output = 20, ack = 21, flow = 22, gap = 23, input = 30, inputAccepted = 31, inputRejected = 32
    case resize = 40, resizeApplied = 41, resizeResponse = 42, snapshot = 50, snapshotChunk = 51, resume = 52
    case leaseRequest = 60, leaseGranted = 61, leaseDenied = 62, leaseRevoked = 63
    case theme = 70, geometry = 71, hostTheme = 72, hostResize = 73, brokerOutput = 74
    case started = 80, interrupted = 81, ended = 82, terminate = 83, historyRequest = 84, historyResponse = 85, historyControl = 86, terminateResponse = 87
    case catalogRequest = 88, catalogResponse = 89, cleanupRequest = 90, cleanupResponse = 91, aiRequest = 92, aiResponse = 93, attachResult = 94
}
