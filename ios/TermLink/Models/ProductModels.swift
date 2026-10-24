// SPDX-License-Identifier: GPL-3.0-only
import Foundation

struct DeviceRecord: Identifiable, Sendable {
    let id: UUID, type: String, name: String, trust: String, exchangeKey: Data, keyEpoch: Int64
    init(_ value: JSONValue) throws {
        guard let id = UUID(uuidString: value["id"].string) else { throw ClientError.invalidResponse("device.id") }
        self.id = id; type = value["type"].string; name = value["name"].string; trust = value["trust_state"].string
        exchangeKey = try Data(base64URL: value["exchange_public_key"].string); keyEpoch = value["key_epoch"].integer
        guard exchangeKey.count == 32 else { throw ClientError.invalidResponse("device key") }
    }
}
struct PC: Identifiable, Sendable {
    let id: UUID, deviceID: UUID, name: String, version: String, online: Bool, routing: Bool, platform: String, capabilities: Set<String>, commands: [JSONValue]
    let keyEpoch: Int64
    init(_ value: JSONValue) throws {
        guard let id = UUID(uuidString: value["id"].string), let device = UUID(uuidString: value["device_id"].string) else { throw ClientError.invalidResponse("pc.id") }
        self.id = id; deviceID = device; name = value["name"].string; version = value["agent_version"].string
        online = value["online"].bool; routing = value["remote_routing_enabled"].bool; platform = value["platform"].string
        capabilities = Set(value["capabilities"].array.map(\.string)); commands = value["terminal_commands"].array
        keyEpoch = value["key_epoch"].integer
    }
}
struct TerminalSession: Identifiable, Sendable {
    let id: UUID, pcID: UUID
    var title: String, shell: String, lifecycle: String, columns: Int, rows: Int, lastSequence: UInt64
    var canOpen: Bool { lifecycle != "UNKNOWN" }
    static func catalog(_ data: ProtoMessage, pc: UUID) throws -> [Self] {
        guard data.bytes(2).count == 16, data.uint(3) > 0, try data.text(5).isEmpty, data.values(4).count <= 4096 else { throw ClientError.protocolViolation("catalog") }
        var ids = Set<UUID>()
        return try data.values(4).map { field in
            let item = try ProtoMessage(field.data), id = try UUID(bytes: item.bytes(1)), state = try item.text(4)
            guard ids.insert(id).inserted, ["RUNNING","EXITED","INTERRUPTED"].contains(state), item.uint(5) > 0, item.uint(5) <= 65535,
                  item.uint(6) > 0, item.uint(6) <= 65535, (item.uint(9) == 1) == (state == "RUNNING") else { throw ClientError.protocolViolation("catalog entry") }
            return Self(id:id,pcID:pc,title:try item.text(3),shell:try item.text(2),lifecycle:state,columns:Int(item.uint(5)),rows:Int(item.uint(6)),lastSequence:item.uint(7))
        }
    }
}
struct AIConversation: Identifiable, Sendable {
    let raw: JSONValue, pcID: UUID
    var id: String { raw["id"].string }
    var title: String { raw["title"].string.isEmpty ? agent + " 对话" : raw["title"].string }
    var agent: String { raw["agentName"].string.isEmpty ? raw["agent"].string : raw["agentName"].string }
    var state: String { raw["state"].string }
    var canControl: Bool { raw["canControl"].bool }
    var messages: [JSONValue] { raw["messages"].array }
    var pending: [JSONValue] { raw["pending"].array }
    var directory: String { raw["directory"].string }
    var capabilities: JSONValue { raw["capabilities"] }
}
struct AgentUpdate: Identifiable, Sendable {
    let raw: JSONValue, pcID: UUID
    var id: String { raw["id"].string }
    var status: String { raw["status"].string }
    var phase: String { raw["phase"].string }
    var percent: Double { min(1,max(0,Double(raw["progress_percent"].integer)/100)) }
    var terminal: Bool { ["SUCCEEDED","FAILED","CANCELLED","EXPIRED","ROLLED_BACK","ROLLBACK_FAILED"].contains(status) }
    var cancelable: Bool { !terminal && ["REQUESTED","PRECHECKING","DOWNLOADING","VERIFIED"].contains(phase) }
}
struct RemoteEntry: Identifiable, Sendable {
    let raw: JSONValue
    var id: String { raw["path"].string }
    var name: String { raw["name"].string }
    var directory: Bool { raw["kind"].string == "directory" }
}
struct DownloadRecord: Codable, Identifiable, Sendable {
    let id: UUID, pcID: UUID, operationID: UUID
    let path: String, filename: String
    let directoryZIP: Bool
    var expectedBytes: UInt64, received: UInt64, status: String, digest: Data
    var error: String?
}
enum ProductTab: String, CaseIterable, Identifiable {
    case devices, cli, ai, settings
    var id: String { rawValue }
    var title: String { switch self { case .devices: "设备"; case .cli: "CLI"; case .ai: "AI"; case .settings: "设置" } }
    var symbol: String { switch self { case .devices:"laptopcomputer.and.iphone"; case .cli:"terminal"; case .ai:"sparkles"; case .settings:"gearshape.fill" } }
}
