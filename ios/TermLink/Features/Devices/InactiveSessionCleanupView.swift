// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI

struct InactiveSessionCleanupView: View {
    let pc: PC
    @ObservedObject var store: InactiveSessionCleanup
    @Environment(\.dismiss) private var dismiss
    @State private var confirming = false
    var body: some View {
        NavigationStack {
            List {
                Section {
                    Text(pc.name).font(.headline)
                    Text(store.status).accessibilityIdentifier("cleanup-status")
                    if let error = store.error { Text(error).foregroundStyle(.orange).accessibilityIdentifier("cleanup-error") }
                    if store.attempted {
                        Text("已清理 \(store.deleted) · 跳过 \(store.skipped) · 未确认 \(store.failed)")
                            .accessibilityIdentifier("cleanup-counts")
                        ProgressView(value: Double(store.processed), total: Double(max(1, store.sessions.count)))
                    }
                    if store.preparing { ProgressView() }
                    if store.running {
                        Button(store.cancelRequested ? "等待当前批次确认" : "停止后续批次") { store.cancel() }
                            .disabled(store.cancelRequested).accessibilityIdentifier("cleanup-cancel")
                    } else if store.ready && store.retryable {
                        Button(store.attempted ? "重试未确认记录" : "清理这 \(store.sessions.count) 条记录", role: .destructive) { confirming = true }
                            .accessibilityIdentifier("cleanup-confirm")
                    }
                    if !store.running && !store.preparing {
                        Button("重新查询电脑记录") { Task { await store.prepare(pc: pc.id) } }
                            .accessibilityIdentifier("cleanup-refresh")
                    }
                } footer: {
                    Text("确认后只处理下列记录。正在运行的会话会跳过；停止后续批次不会撤销已完成的清理。")
                }
                Section("本次确认范围（\(store.sessions.count)）") {
                    ForEach(store.sessions) { session in
                        HStack {
                            VStack(alignment: .leading) {
                                Text(session.title.isEmpty ? session.shell : session.title)
                                Text(session.id.uuidString.lowercased()).font(.caption2).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Text(outcome(session)).font(.caption).foregroundStyle(.secondary)
                        }.accessibilityIdentifier("cleanup-session-" + session.id.uuidString.lowercased())
                    }
                }
            }
            .navigationTitle("清理已结束会话")
            .toolbar { Button("关闭") { dismiss() }.disabled(store.running || store.preparing) }
            .interactiveDismissDisabled(store.running || store.preparing)
            .alert("确认清理这些已结束记录？", isPresented: $confirming) {
                Button(store.attempted ? "确认重试原记录" : "确认清理", role: .destructive) { Task { await store.run() } }
                Button("取消", role: .cancel) {}
            } message: {
                Text("仅处理当前列出的电脑和会话编号，不会结束正在运行的 Shell。")
            }
            .task(id: pc.id) { await store.prepare(pc: pc.id) }
        }
    }
    private func outcome(_ session: TerminalSession) -> String {
        switch store.outcomes[session.id] {
        case .deleted: "已清理"
        case .notFound: "电脑确认不存在"
        case .skippedActive: "已运行，跳过"
        case .skippedMissing: "目录中已不存在，跳过"
        case .failed: "结果待确认"
        case .none: session.lifecycle
        }
    }
}
