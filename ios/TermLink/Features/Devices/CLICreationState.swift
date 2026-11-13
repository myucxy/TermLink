// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// Scope and machine identity are immutable for a submitted request. Names are
/// presentation only; an account or key change must never rebind pending work.
struct CLICreationBinding: Codable, Equatable, Sendable {
    let scope: String, pcID: UUID, pcDeviceID: UUID, requesterID: UUID, keyEpoch: Int64, platform: String
}
struct CLICreationContext: Sendable {
    let binding: CLICreationBinding, commands: [JSONValue], canBrowse: Bool
}
struct CLICreationRequest: Codable, Equatable, Sendable {
    let version: Int, binding: CLICreationBinding, idempotency: UUID, createdAt: Date, path: String, command: JSONValue
    var operationID: UUID?, status: String, sessionID: UUID?, failure: String?
    var commandID: String { command["id"].string }
    var complete: Bool { ["SUCCEEDED", "FAILED", "CANCELLED", "EXPIRED", "REJECTED"].contains(status) }
    var body: JSONValue {
        .object(["operation": .string("OPEN_TERMINAL"), "path": .string(path),
                 command["source"].string == "legacy" ? "shell_type" : "terminal_command_id": .string(commandID)])
    }
    var route: String { "/v1/pcs/\(binding.pcID)/file-operations" }
    func validate() throws {
        guard version == 1, binding.keyEpoch > 0, !binding.scope.isEmpty,
              [binding.pcID,binding.pcDeviceID,binding.requesterID,idempotency].allSatisfy({$0 != UUID.nilUUID}),
              operationID != UUID.nilUUID, sessionID != UUID.nilUUID,
              ["UNCONFIRMED","REJECTED"].contains(status) || operationID != nil, !commandID.isEmpty, commandID.utf8.count <= 128,
              ["UNCONFIRMED","PENDING","PROCESSING","SUCCEEDED","FAILED","CANCELLED","EXPIRED","REJECTED"].contains(status),
              status != "SUCCEEDED" || sessionID != nil else { throw ClientError.invalidResponse("CLI saved request") }
        try CLIPath.validate(path, platform: binding.platform)
    }
    func requireAuthority(_ current: CLICreationContext) throws {
        guard current.binding == binding, current.commands.contains(command) else {
            throw ClientError.unavailable("电脑身份、终端命令或授权已改变。已保留原请求，不会改投其他电脑。")
        }
    }
    func canReplayOriginal(at now: Date) -> Bool {
        // Server idempotency retention is 24h. Leave a conservative margin and
        // reject a clock that moved behind the persisted submission time.
        let age = now.timeIntervalSince(createdAt)
        return operationID == nil && age >= 0 && age < 23 * 60 * 60
    }
    func applying(_ response: JSONValue) throws -> Self {
        let value = response["file_operation"]
        guard let id = UUID(uuidString: value["id"].string), id != UUID.nilUUID, operationID == nil || operationID == id,
              UUID(uuidString: value["pc_id"].string) == binding.pcID,
              UUID(uuidString: value["requester_device_id"].string) == binding.requesterID,
              value["operation"].string == "OPEN_TERMINAL", value["path"].string == path,
              value["terminal_command_id"].string == commandID || value["terminal_command_id"] == .null && value["shell_type"].string == commandID,
              ["PENDING","PROCESSING","SUCCEEDED","FAILED","CANCELLED","EXPIRED"].contains(value["status"].string) else {
            throw ClientError.protocolViolation("CLI operation identity")
        }
        var next = self; next.operationID = id; next.status = value["status"].string
        next.failure = value["error"].string.isEmpty ? nil : String(value["error"].string.prefix(512))
        if next.status == "SUCCEEDED" {
            guard let session = UUID(uuidString: value["result"]["session_id"].string), session != UUID.nilUUID else {
                throw ClientError.protocolViolation("CLI created session")
            }
            next.sessionID = session
        }
        return next
    }
}
struct CLIRecentDirectory: Codable, Equatable, Identifiable, Sendable {
    let binding: CLICreationBinding, path: String
    var id: String { path }
}
struct CLICreationArchive: Codable, Sendable {
    var version = 1
    var request: CLICreationRequest?
    var recent: [CLIRecentDirectory] = []
}
enum CLIPath {
    static func validate(_ path: String, platform: String, allowRoots: Bool = false) throws {
        if allowRoots && path.isEmpty { return }
        guard !path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, path.utf8.count <= 4096,
              !path.contains("\0"), !path.contains("\r"), !path.contains("\n") else { throw ClientError.unavailable("请输入电脑上的有效绝对目录。") }
        let absolute: Bool
        if platform == "macos" { absolute = path.hasPrefix("/") }
        else if platform == "windows" {
            let chars = Array(path)
            absolute = chars.count >= 3 && chars[0].isASCII && chars[0].isLetter && chars[1] == ":" && (chars[2] == "\\" || chars[2] == "/") ||
                path.hasPrefix("\\\\") && path.dropFirst(2).split(separator: "\\").count >= 2
        } else { absolute = false }
        guard absolute else { throw ClientError.unavailable("工作目录必须是所选电脑上的绝对路径，不能使用 ~ 或相对路径。") }
    }
}
struct CLIDirectoryPage: Sendable {
    let path: String, parent: String?, entries: [RemoteEntry], nextOffset: Int64?
    static func decode(_ value: JSONValue, platform: String, offset: Int64) throws -> Self {
        let result = value["result"]
        guard case .string(let currentPath)=result["current_path"],case .array = result["entries"],
              result["parent_path"] == .null || { if case .string=result["parent_path"] {return true};return false }() else {
            throw ClientError.invalidResponse("directory page")
        }
        try CLIPath.validate(currentPath, platform: platform, allowRoots: true)
        let parent = result["parent_path"] == .null ? nil : result["parent_path"].string
        if let parent { try CLIPath.validate(parent, platform: platform, allowRoots: true) }
        let next: Int64?
        if result["next_offset"] == .null { next = nil }
        else {
            guard case .number(let number) = result["next_offset"], number.isFinite, number.rounded() == number,
                  number > Double(offset), number < Double(Int64.max) else { throw ClientError.invalidResponse("directory offset") }
            next = Int64(number)
        }
        guard result["entries"].array.count <= 500 else { throw ClientError.limitExceeded }
        var directories: [RemoteEntry] = [], seen = Set<String>()
        for row in result["entries"].array {
            guard ["file","directory"].contains(row["kind"].string), !row["name"].string.isEmpty, row["name"].string.utf8.count <= 1024 else {
                throw ClientError.invalidResponse("directory entry")
            }
            try CLIPath.validate(row["path"].string, platform: platform)
            if row["kind"].string == "directory", seen.insert(row["path"].string).inserted { directories.append(RemoteEntry(raw: row)) }
        }
        return .init(path: result["current_path"].string, parent: parent, entries: directories, nextOffset: next)
    }
}

private extension UUID { static let nilUUID = UUID(uuid: (0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0)) }
