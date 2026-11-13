// SPDX-License-Identifier: GPL-3.0-only
import Foundation

struct AIArtifactSource: Codable, Hashable, Sendable {
    let pcID: UUID
    let hostID: String
    let sessionID: String
    let directory: String
    let pathStyle: String
    let artifactID: String
    let version: String
    let filename: String

    init(session: AIConversation, hostID: String, artifactID: String, version: String) throws {
        self.pcID = session.pcID; self.hostID = hostID; self.sessionID = session.id
        self.directory = session.directory; self.pathStyle = session.capabilities["pathStyle"].string
        self.artifactID = try aiArtifactRelativePath(directory: directory, value: artifactID, pathStyle: pathStyle)
        self.version = version
        // Host POSIX backslashes are data. Only '/' is a separator after the host-aware normalization.
        self.filename = String(self.artifactID.split(separator: "/").last ?? "")
        try validate()
        guard session.capabilities["artifacts"].bool else { throw ClientError.readOnly }
    }
    func validate() throws {
        guard UUID(uuidString: hostID) != nil, UUID(uuidString: sessionID) != nil,
              !version.isEmpty, version.utf16.count <= 100,
              !version.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
              !filename.isEmpty, filename != ".", filename != "..", filename.utf8.count <= 255,
              !filename.contains("/"), !filename.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
              try aiArtifactRelativePath(directory: directory, value: artifactID, pathStyle: pathStyle) == artifactID else {
            throw ClientError.invalidResponse("artifact download identity")
        }
    }
    func matches(_ session: AIConversation, host: String) -> Bool {
        pcID == session.pcID && hostID == host && sessionID == session.id && directory == session.directory &&
            pathStyle == session.capabilities["pathStyle"].string && session.capabilities["artifacts"].bool &&
            (try? aiArtifactRelativePath(directory: session.directory, value: artifactID, pathStyle: pathStyle)) == artifactID
    }
}

struct AIArtifactDownload: Codable, Identifiable, Equatable, Sendable {
    enum Status: String, Codable, Sendable {
        case preparing, downloading, paused, restored, cancelled, failed, completed
        var active: Bool { self == .preparing || self == .downloading }
        var title: String {
            switch self {
            case .preparing: "正在确认原会话"
            case .downloading: "下载中"
            case .paused: "已暂停"
            case .restored: "已恢复 · 待确认"
            case .cancelled: "已取消"
            case .failed: "下载失败"
            case .completed: "已下载"
            }
        }
    }
    let id: UUID
    let source: AIArtifactSource
    let createdAt: Date
    var status: Status = .preparing
    var expectedBytes: Int64?
    var received: Int64 = 0
    var sealedBytes: Int64 = 0
    var error: String?
    var retryable: Bool { !status.active && status != .completed }
    func validate() throws {
        try source.validate()
        guard received >= 0, received <= AIArtifactChunk.maximumFileBytes, sealedBytes >= 0,
              sealedBytes <= AIArtifactChunk.maximumFileBytes + 1024 * 1024,
              expectedBytes.map({ (0...AIArtifactChunk.maximumFileBytes).contains($0) && received <= $0 }) ?? (received == 0),
              status != .completed || expectedBytes == received,
              (received == 0) == (sealedBytes == 0),
              error.map({ $0.utf8.count <= 1024 }) ?? true else { throw ClientError.invalidResponse("artifact download metadata") }
    }
}

struct AIArtifactChunk: Sendable {
    static let maximumFileBytes: Int64 = 50 * 1024 * 1024
    static let maximumPageBytes = 64 * 1024 // Current host artifact.read contract.
    let bytes: Data
    let size: Int64
    let nextOffset: Int64
    static func decode(_ value: JSONValue, source: AIArtifactSource, offset: Int64, expectedSize: Int64?) throws -> Self {
        func number(_ key: String) throws -> Int64 {
            guard case .number(let raw) = value[key], raw.isFinite, raw >= 0,
                  raw <= Double(maximumFileBytes), raw.rounded(.towardZero) == raw else {
                throw ClientError.protocolViolation("artifact range")
            }
            return Int64(raw)
        }
        guard value["id"] == .string(source.artifactID), value["version"] == .string(source.version),
              case .string(let encoded) = value["data"], encoded.utf8.count <= (maximumPageBytes + 2) / 3 * 4 else {
            throw ClientError.protocolViolation("artifact identity")
        }
        let bytes = try Data(base64URL: encoded), size = try number("size"), next = try number("nextOffset")
        guard try number("offset") == offset, offset <= size, expectedSize == nil || expectedSize == size,
              bytes.count <= maximumPageBytes, next == offset + Int64(bytes.count), next <= size,
              !bytes.isEmpty || offset == size else { throw ClientError.protocolViolation("artifact range") }
        return .init(bytes: bytes, size: size, nextOffset: next)
    }
}
