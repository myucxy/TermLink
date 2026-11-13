// SPDX-License-Identifier: GPL-3.0-only
import Combine
import Foundation

struct SessionCleanupBinding: Equatable, Sendable {
    let scope: String, generation: UUID, pcID: UUID, pcDeviceID: UUID, requesterID: UUID, keyEpoch: Int64
}
struct SessionCleanupContext: Sendable {
    let binding: SessionCleanupBinding
    let sessions: [TerminalSession]
}
enum SessionCleanupOutcome: String, Sendable {
    case deleted = "DELETED", notFound = "NOT_FOUND", skippedActive = "SKIPPED_ACTIVE", failed = "FAILED", skippedMissing
    var completed: Bool { self == .deleted || self == .notFound }
    var skipped: Bool { self == .skippedActive || self == .skippedMissing }
}
struct SessionCleanupReply: Sendable {
    let outcomes: [UUID: SessionCleanupOutcome]
    let mirrorPending: Bool
    static func decode(_ message: ProtoMessage, requestID: UUID, sessionIDs: [UUID]) throws -> Self {
        try message.validateSingular([1, 3])
        guard message.values(1).count == 1, message.values(1).first?.wire == 2,
              message.bytes(1) == requestID.bytes, !sessionIDs.isEmpty, sessionIDs.count <= 64,
              Set(sessionIDs).count == sessionIDs.count, message.values(2).count <= 64,
              message.values(3).allSatisfy({ $0.wire == 2 }) else { throw ClientError.protocolViolation("cleanup response") }
        let code = try message.text(3)
        guard code.isEmpty || code == "MIRROR_SYNC_PENDING" else { throw ClientError.unavailable("电脑未确认清理结果，请重新查询原电脑记录。") }
        let expected = Set(sessionIDs)
        var outcomes: [UUID: SessionCleanupOutcome] = [:]
        for field in message.values(2) {
            guard field.wire == 2 else { throw ClientError.protocolViolation("cleanup result") }
            let item = try ProtoMessage(field.data)
            try item.validateSingular([1, 2])
            guard item.values(1).count == 1, item.values(2).count == 1,
                  item.values(1).first?.wire == 2, item.values(2).first?.wire == 2 else { throw ClientError.protocolViolation("cleanup result") }
            let id = try UUID(bytes: item.bytes(1))
            guard expected.contains(id), outcomes[id] == nil,
                  let status = SessionCleanupOutcome(rawValue: try item.text(2)), status != .skippedMissing else {
                throw ClientError.protocolViolation("cleanup result")
            }
            outcomes[id] = status
        }
        guard Set(outcomes.keys) == expected else { throw ClientError.protocolViolation("cleanup missing result") }
        return .init(outcomes: outcomes, mirrorPending: code == "MIRROR_SYNC_PENDING")
    }
}

/// A single account/PC-bound operation. Only the IDs displayed before confirmation can be submitted.
@MainActor final class InactiveSessionCleanup: ObservableObject {
    @Published private(set) var sessions: [TerminalSession] = []
    @Published private(set) var outcomes: [UUID: SessionCleanupOutcome] = [:]
    @Published private(set) var preparing = false
    @Published private(set) var running = false
    @Published private(set) var cancelRequested = false
    @Published private(set) var error: String?
    @Published private(set) var mirrorPending = false
    @Published private(set) var needsRefresh = false
    @Published private(set) var attempted = false
    private var binding: SessionCleanupBinding?
    private var generation = UUID()
    private let context: (UUID) throws -> SessionCleanupBinding
    private let refresh: (UUID) async throws -> SessionCleanupContext
    private let send: (SessionCleanupBinding, [UUID]) async throws -> (requestID: UUID, response: ProtoMessage)
    private let acknowledged: (SessionCleanupBinding, Set<UUID>) throws -> Void
    private static let maximumSessions = 4096

    init(context: @escaping (UUID) throws -> SessionCleanupBinding,
         refresh: @escaping (UUID) async throws -> SessionCleanupContext,
         send: @escaping (SessionCleanupBinding, [UUID]) async throws -> (requestID: UUID, response: ProtoMessage),
         acknowledged: @escaping (SessionCleanupBinding, Set<UUID>) throws -> Void) {
        self.context = context; self.refresh = refresh; self.send = send; self.acknowledged = acknowledged
    }
    static func inactive(_ state: String) -> Bool { ["EXITED", "INTERRUPTED", "ENDED", "FAILED"].contains(state) }
    var deleted: Int { outcomes.values.filter(\.completed).count }
    var failed: Int { outcomes.values.filter { $0 == .failed }.count }
    var skipped: Int { outcomes.values.filter(\.skipped).count }
    var processed: Int { outcomes.count }
    var retryable: Bool { sessions.contains { outcomes[$0.id] == nil || outcomes[$0.id] == .failed } }
    var ready: Bool { binding != nil && !preparing && !running && !sessions.isEmpty }
    var status: String {
        if preparing { return "正在重新确认电脑、管理权限与会话记录…" }
        if running { return cancelRequested ? "当前批次正在确认；确认后停止后续清理。" : "正在确认电脑的清理回执…" }
        if mirrorPending { return "电脑已确认部分本地清理，但服务端目录仍待同步。请稍后重新查询。" }
        if needsRefresh { return "清理回执已记录，电脑目录仍待重新确认。" }
        if cancelRequested { return "已停止后续批次；当前批次结果已保留。" }
        if attempted { return retryable ? "部分记录尚未确认，可重试原确认范围。" : "已完成本次确认范围。" }
        return sessions.isEmpty ? "没有可清理的已结束记录。" : "只清理下列记录，不会结束正在运行的 Shell。"
    }
    func prepare(pc: UUID) async {
        guard !preparing, !running else { return }
        preparing = true; let owner = UUID(); generation = owner
        sessions = []; outcomes = [:]; binding = nil; error = nil; attempted = false
        cancelRequested = false; mirrorPending = false; needsRefresh = false
        defer { if generation == owner { preparing = false } }
        do {
            let initial = try context(pc), fresh = try await refresh(pc)
            guard generation == owner, initial == fresh.binding, try context(pc) == initial else { throw ClientError.readOnly }
            guard fresh.sessions.count <= Self.maximumSessions, Set(fresh.sessions.map(\.id)).count == fresh.sessions.count,
                  fresh.sessions.allSatisfy({ $0.pcID == pc }) else { throw ClientError.protocolViolation("cleanup catalog") }
            binding = initial; sessions = fresh.sessions.filter { Self.inactive($0.lifecycle) }
        } catch { if generation == owner { self.error = Self.safe(error) } }
    }
    func cancel() { if running { cancelRequested = true } }
    func retire() {
        generation = UUID(); cancelRequested = true; binding = nil; sessions = []; outcomes = [:]; error = nil
        preparing = false
        // Keep the in-flight fence until the bounded request settles.
    }
    func run() async {
        guard ready, retryable, let fixed = binding else { return }
        running = true; attempted = true; cancelRequested = false; error = nil; mirrorPending = false; needsRefresh = false
        let owner = generation
        defer { running = false }
        let confirmed = sessions.map(\.id).filter { outcomes[$0] == nil || outcomes[$0] == .failed }
        var offset = 0
        while offset < confirmed.count {
            if cancelRequested || Task.isCancelled { cancelRequested = true; break }
            var submitted: [UUID] = []
            do {
                guard owner == generation, try context(fixed.pcID) == fixed else { throw ClientError.readOnly }
                let fresh = try await refresh(fixed.pcID)
                guard owner == generation, fresh.binding == fixed, try context(fixed.pcID) == fixed,
                      fresh.sessions.count <= Self.maximumSessions, Set(fresh.sessions.map(\.id)).count == fresh.sessions.count,
                      fresh.sessions.allSatisfy({ $0.pcID == fixed.pcID }) else { throw ClientError.readOnly }
                if cancelRequested || Task.isCancelled { cancelRequested = true; break }
                let current = Dictionary(uniqueKeysWithValues: fresh.sessions.map { ($0.id, $0) })
                let batch = Array(confirmed[offset..<min(offset + 64, confirmed.count)])
                offset += batch.count
                for id in batch {
                    guard let session = current[id] else { outcomes[id] = .skippedMissing; continue }
                    if Self.inactive(session.lifecycle) { submitted.append(id) } else { outcomes[id] = .skippedActive }
                }
                if submitted.isEmpty { continue }
                // An unstructured owned task deliberately settles the in-flight batch even if its UI task is cancelled.
                let settle = Task { @MainActor in try await self.send(fixed, submitted) }
                let packet = try await settle.value
                guard owner == generation, try context(fixed.pcID) == fixed else { throw ClientError.readOnly }
                let reply = try SessionCleanupReply.decode(packet.response, requestID: packet.requestID, sessionIDs: submitted)
                try acknowledged(fixed, Set(reply.outcomes.filter { $0.value.completed }.keys))
                outcomes.merge(reply.outcomes) { _, next in next }
                mirrorPending = reply.mirrorPending
                do {
                    let after = try await refresh(fixed.pcID)
                    guard owner == generation, after.binding == fixed, try context(fixed.pcID) == fixed else { throw ClientError.readOnly }
                } catch {
                    guard owner == generation else { break }
                    needsRefresh = true
                    self.error = "电脑目录尚未重新确认，已保留本批清理回执。"
                    break
                }
                if mirrorPending { break }
            } catch {
                guard owner == generation else { break }
                submitted.forEach { outcomes[$0] = .failed }
                self.error = Self.safe(error)
                // No optimistic deletion after a timeout; a later explicit refresh/retry uses the same confirmed IDs.
                break
            }
        }
    }
    private static func safe(_ error: Error) -> String {
        if let error = error as? ClientError { return error.localizedDescription }
        if let error = error as? APIError, error.status == 403 { return "此设备没有管理权限，不能清理电脑记录。" }
        return "清理结果尚未确认，请重新查询原电脑记录。"
    }
}

/// The grants endpoint already includes implicit MANAGE recipients when the server enables auto trust.
enum SessionCleanupAuthorization {
    static func validate(_ value: JSONValue, binding: SessionCleanupBinding, now: Date = Date()) throws {
        guard value["pc_id"].string.lowercased() == binding.pcID.uuidString.lowercased(),
              value["key_epoch"].integer == binding.keyEpoch, value["items"].array.count <= 4096 else { throw ClientError.readOnly }
        let recipients = value["items"].array.filter { $0["device_id"].string.lowercased() == binding.requesterID.uuidString.lowercased() }
        guard recipients.count == 1, let actor = recipients.first, actor["permission"].string == "MANAGE",
              ["ANDROID", "IOS"].contains(actor["device_type"].string) else { throw ClientError.unavailable("此设备没有管理权限，不能清理电脑记录。") }
        if let expiry = actor.object["expires_at"], expiry != .null {
            let fraction = ISO8601DateFormatter(); fraction.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            guard let date = fraction.date(from: expiry.string) ?? ISO8601DateFormatter().date(from: expiry.string), date > now else { throw ClientError.readOnly }
        }
    }
}
