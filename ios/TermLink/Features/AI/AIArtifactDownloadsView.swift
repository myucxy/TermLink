// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI

struct AIArtifactDownloadsView: View {
    @ObservedObject var store: AIArtifactDownloadStore
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        List {
            if store.records.isEmpty {
                ContentUnavailableView("暂无 AI 成果下载", systemImage: "arrow.down.doc", description: Text("在 AI 会话的文件或成果中选择下载。"))
            }
            ForEach(store.records) { record in
                VStack(alignment: .leading, spacing: 8) {
                    Text(record.source.filename).font(.headline)
                    Text(record.source.directory).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    Text(record.status.title).font(.caption)
                    if let size = record.expectedBytes {
                        ProgressView(value: Double(record.received), total: Double(max(1, size)))
                        Text("\(record.received.formatted(.byteCount(style: .file))) / \(size.formatted(.byteCount(style: .file)))").font(.caption2)
                    }
                    if let error = record.error { Text(error).font(.caption).foregroundStyle(.orange) }
                    HStack {
                        if store.isBusy(record.id) {
                            Button("暂停") { Task { await store.pause(record.id) } }
                            Button("取消下载", role: .destructive) { Task { await store.cancel(record.id) } }
                        } else if record.status == .completed {
                            Button("分享或保存文件") { Task { await store.share(record.id) } }
                        } else {
                            Button("确认原会话并重试") { store.retry(record.id) }.disabled(!store.available)
                        }
                        if !store.isBusy(record.id) { Button("移除记录", role: .destructive) { Task { await store.remove(record.id) } } }
                    }.font(.caption).buttonStyle(.borderless)
                    if record.status == .completed {
                        Text("已核对主机返回的版本与大小。主机未提供完整文件哈希。").font(.caption2).foregroundStyle(.secondary)
                    }
                }.padding(.vertical, 4)
            }
        }
        .navigationTitle("AI 成果下载")
        .toolbar { Button("关闭") { dismiss() } }
        .alert("下载操作未完成", isPresented: Binding(get: { store.error != nil }, set: { if !$0 { store.error = nil } })) {
            Button("关闭", role: .cancel) { store.error = nil }
        } message: { Text(store.error ?? "") }
        .sheet(isPresented: Binding(get: { store.exportURL != nil }, set: { if !$0 { store.dismissShare() } })) {
            if let url = store.exportURL { ActivityShareView(url: url) }
        }
    }
}

private struct AIArtifactDownloadsPresentation: ViewModifier {
    @ObservedObject var store: AIArtifactDownloadStore
    func body(content: Content) -> some View {
        content.toolbar {
            Button("AI 下载", systemImage: "arrow.down.doc") { store.presented = true }
                .disabled(!store.available).accessibilityIdentifier("ai-artifact-downloads")
        }
        .sheet(isPresented: $store.presented) { NavigationStack { AIArtifactDownloadsView(store: store) } }
    }
}
extension View {
    func aiArtifactDownloads(workspace: AIWorkspace) -> some View {
        modifier(AIArtifactDownloadsPresentation(store: workspace.artifactDownloads))
    }
}
