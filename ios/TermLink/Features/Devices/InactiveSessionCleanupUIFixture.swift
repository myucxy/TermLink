// SPDX-License-Identifier: GPL-3.0-only
#if DEBUG
import SwiftUI

/// Process-only isolated UI fixture: no API client, secrets, filesystem state or live route.
struct InactiveSessionCleanupUIFixture: View {
    @StateObject private var model = Model()
    @State private var showing = false
    var body: some View {
        VStack(spacing: 16) {
            Text("已发送批次：\(model.batches)").accessibilityIdentifier("cleanup-fixture-batches")
            Text("已确认清理：\(model.deleted.count)").accessibilityIdentifier("cleanup-fixture-deleted")
            Button("打开清理测试") { showing = true }
        }
        .sheet(isPresented: $showing) { InactiveSessionCleanupView(pc: model.pc, store: model.store) }
    }
    @MainActor private final class Model: ObservableObject {
        let pcID = UUID(), deviceID = UUID(), requester = UUID(), generation = UUID()
        @Published var batches = 0
        @Published var deleted = Set<UUID>()
        let batching = ProcessInfo.processInfo.arguments.contains("--cleanup-batch-fixture")
        lazy var pc = try! PC(.object(["id": .string(pcID.uuidString), "device_id": .string(deviceID.uuidString),
            "name": .string("清理测试 Mac"), "platform": .string("macos"), "key_epoch": .number(1),
            "online": .bool(true), "remote_routing_enabled": .bool(true),
            "capabilities": .array(["host_session_catalog_v1", "host_session_cleanup_v1"].map(JSONValue.string))]))
        lazy var binding = SessionCleanupBinding(scope: "isolated-ui", generation: generation, pcID: pcID,
            pcDeviceID: deviceID, requesterID: requester, keyEpoch: 1)
        lazy var records: [TerminalSession] = (0..<(batching ? 65 : 3)).map {
            .init(id: UUID(), pcID: pcID, title: "已结束记录 \($0 + 1)", shell: "zsh", lifecycle: "EXITED", columns: 100, rows: 30, lastSequence: 1)
        } + [.init(id: UUID(), pcID: pcID, title: "正在运行，禁止清理", shell: "zsh", lifecycle: "RUNNING", columns: 100, rows: 30, lastSequence: 1)]
        lazy var store = InactiveSessionCleanup(context: { [unowned self] id in
            guard id == pcID else { throw ClientError.readOnly }; return binding
        }, refresh: { [unowned self] _ in .init(binding: binding, sessions: records) }, send: { [unowned self] _, ids in
            batches += 1
            if batching { try await Task.sleep(for: .seconds(2)) }
            let request = UUID(); var reply = ProtoMessage(); reply.set(1, request.bytes)
            for (offset, id) in ids.enumerated() {
                var row = ProtoMessage(); row.set(1, id.bytes)
                row.set(2, batching || offset == 0 ? "DELETED" : offset == 1 ? "SKIPPED_ACTIVE" : "FAILED")
                reply.add(2, row.encoded())
            }
            return (request, reply)
        }, acknowledged: { [unowned self] _, ids in deleted.formUnion(ids) })
    }
}
#endif
