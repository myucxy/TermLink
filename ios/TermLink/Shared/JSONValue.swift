// SPDX-License-Identifier: GPL-3.0-only
import Foundation

enum JSONValue: Codable, Hashable, Sendable {
    case object([String: JSONValue]), array([JSONValue]), string(String), number(Double), bool(Bool), null
    init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { self = .null }
        else if let v = try? c.decode(Bool.self) { self = .bool(v) }
        else if let v = try? c.decode(String.self) { self = .string(v) }
        else if let v = try? c.decode(Double.self) { self = .number(v) }
        else if let v = try? c.decode([JSONValue].self) { self = .array(v) }
        else { self = .object(try c.decode([String: JSONValue].self)) }
    }
    func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .object(let v): try c.encode(v)
        case .array(let v): try c.encode(v)
        case .string(let v): try c.encode(v)
        case .number(let v): try c.encode(v)
        case .bool(let v): try c.encode(v)
        case .null: try c.encodeNil()
        }
    }
    subscript(_ key: String) -> JSONValue { object[key] ?? .null }
    var object: [String: JSONValue] { if case .object(let v) = self { return v }; return [:] }
    var array: [JSONValue] { if case .array(let v) = self { return v }; return [] }
    var string: String { if case .string(let v) = self { return v }; return "" }
    var bool: Bool { if case .bool(let v) = self { return v }; return false }
    var integer: Int64 { if case .number(let v) = self, v.isFinite, v.rounded() == v, v >= Double(Int64.min), v < Double(Int64.max) { return Int64(v) }; return Int64(string) ?? 0 }
    func required(_ key: String) throws -> String {
        let value = self[key].string
        guard !value.isEmpty else { throw ClientError.invalidResponse(key) }
        return value
    }
    func data() throws -> Data { let e = JSONEncoder(); e.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]; return try e.encode(self) }
    static func decode(_ data: Data, maximum: Int = 1_048_576) throws -> JSONValue {
        guard data.count <= maximum else { throw ClientError.limitExceeded }
        return try JSONDecoder().decode(Self.self, from: data)
    }
}

enum ClientError: LocalizedError, Equatable {
    case invalidResponse(String), invalidURL, limitExceeded, authentication, unavailable(String), protocolViolation(String), readOnly, cancelled
    var errorDescription: String? {
        switch self {
        case .invalidResponse(let f): return "响应字段无效：\(f)"
        case .invalidURL: return "服务器地址必须为不含凭据的 HTTPS 地址。"
        case .limitExceeded: return "内容超过安全上限。"
        case .authentication: return "登录已过期，请重新授权。"
        case .unavailable(let s): return s
        case .protocolViolation(let s): return "协议校验失败：\(s)"
        case .readOnly: return "当前会话为只读或状态尚未确认。"
        case .cancelled: return "操作已取消。"
        }
    }
}

extension Data {
    var base64URL: String { base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "") }
    init(base64URL: String) throws {
        let s = base64URL.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        guard let d = Data(base64Encoded: s + String(repeating: "=", count: (4 - s.count % 4) % 4)) else { throw ClientError.invalidResponse("base64url") }
        self = d
    }
    var hex: String { map { String(format: "%02x", $0) }.joined() }
    static func bigEndian(_ value: UInt64) -> Data { var v = value.bigEndian; return Swift.withUnsafeBytes(of: &v) { Data($0) } }
}
extension UUID {
    var bytes: Data { var v = uuid; return withUnsafeBytes(of: &v) { Data($0) } }
    init(bytes: Data) throws {
        guard bytes.count == 16, bytes.contains(where: { $0 != 0 }) else { throw ClientError.invalidResponse("UUID") }
        let a = Array(bytes)
        self.init(uuid: (a[0],a[1],a[2],a[3],a[4],a[5],a[6],a[7],a[8],a[9],a[10],a[11],a[12],a[13],a[14],a[15]))
    }
}
