// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI

struct AIComputerFilePicker: View {
    @ObservedObject var workspace: AIWorkspace
    let pc: UUID
    var initialPath: String
    var selectFile = true
    var selected: (String) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var path = ""
    @State private var parent: String?
    @State private var offset: Int64 = 0
    @State private var next: Int64?
    @State private var entries: [RemoteEntry] = []
    @State private var busy = false
    @State private var error: String?

    var body: some View {
        List {
            Section {
                Text(path.isEmpty ? "电脑磁盘" : path).font(.caption).textSelection(.enabled)
                if let parent { Button("上一级", systemImage: "arrow.up") { navigate(parent) }.disabled(busy) }
                if !selectFile, !path.isEmpty { Button("选择此目录") { selected(path); dismiss() }.disabled(busy) }
            }
            if busy { ProgressView() }
            if let error { Text(error).foregroundStyle(.orange); Button("重试") { load() } }
            ForEach(entries) { entry in
                Button {
                    if entry.directory { navigate(entry.id) }
                    else if selectFile { selected(entry.id); dismiss() }
                } label: { Label(entry.name, systemImage: entry.directory ? "folder" : "doc") }
                .disabled(busy)
            }
            if let next { Button("加载更多") { offset = next; load() }.disabled(busy) }
        }
        .navigationTitle(selectFile ? "选择电脑文件" : "选择电脑目录").navigationBarTitleDisplayMode(.inline)
        .toolbar { Button("取消") { dismiss() } }
        .task { path = initialPath; load() }
    }
    private func navigate(_ target: String) { path = target; offset = 0; entries = []; load() }
    private func load() {
        guard !busy else { return }; busy = true; error = nil
        let requested = path, requestedOffset = offset
        Task {
            defer { busy = false }
            do {
                let page = try await workspace.directories(pc: pc, path: requested, offset: requestedOffset, includeFiles: selectFile)
                guard path == requested, offset == requestedOffset else { return }
                path = page["currentPath"].string; parent = page["parentPath"] == .null ? nil : page["parentPath"].string
                var ids = Set<String>()
                entries = ((requestedOffset == 0 ? [] : entries) + page["entries"].array.map(RemoteEntry.init)).filter { ids.insert($0.id).inserted }
                next = page["nextOffset"] == .null ? nil : page["nextOffset"].integer
            } catch { self.error = error.localizedDescription }
        }
    }
}
