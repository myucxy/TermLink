// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI
import UniformTypeIdentifiers

struct RemoteFilesView: View {
    @ObservedObject var store: RemoteFileStore
    let pc: PC
    var initialPath = ""
    @EnvironmentObject private var product: ProductStore
    @Environment(\.dismiss) private var dismiss
    @State private var creatingAI: AICreateLocation?
    @State private var createdAI = false
    private var loaded: Bool { store.pcID == pc.id && !store.busy && store.error == nil }
    private var canCreateAI: Bool {
        product.pcs.contains { $0.id == pc.id && AICreateLocation.eligible($0) }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Button("上一级", systemImage: "arrow.up") { navigate(store.parentPath ?? "") }
                    .disabled(!loaded || store.parentPath == nil)
                Text(store.pcID == pc.id && !store.path.isEmpty ? store.path : "电脑磁盘").font(.caption).lineLimit(2)
                Spacer()
                Button("收藏", systemImage: store.currentFavorites.contains(store.path) ? "star.fill" : "star") { store.toggleFavorite() }
                    .disabled(!loaded || store.path.isEmpty)
                Menu {
                    directoryActions(path: store.path)
                } label: { Label("当前目录操作", systemImage: "ellipsis.circle").labelStyle(.iconOnly) }
                    .disabled(!loaded || store.path.isEmpty)
            }.padding(10)
            HStack {
                TextField("筛选名称", text: $store.query).textFieldStyle(.roundedBorder)
                Button("搜索") { navigate(store.pcID == pc.id ? store.path : initialPath) }.disabled(store.busy)
            }.padding(.horizontal)
            if store.busy { ProgressView().padding() }
            if let error = store.error { Text(error).foregroundStyle(.red).font(.caption).padding() }
            List {
                if store.pcID == pc.id {
                    if !store.currentFavorites.isEmpty {
                        Section("收藏目录") {
                            ForEach(store.currentFavorites, id: \.self) { path in Button(path) { navigate(path) }.disabled(store.busy) }
                        }
                    }
                    Section {
                        ForEach(store.entries) { entry in
                            HStack {
                                Button { if entry.directory { navigate(entry.id) } } label: {
                                    Label(entry.name, systemImage: entry.directory ? "folder" : "doc")
                                        .frame(maxWidth: .infinity, alignment: .leading)
                                }.disabled(!loaded)
                                Menu {
                                    if entry.directory { directoryActions(path: entry.id) }
                                    else { Button("下载文件") { download(entry) } }
                                } label: { Image(systemName: "ellipsis").frame(width: 40, height: 40) }.disabled(!loaded)
                            }
                        }
                        if let offset = store.nextOffset {
                            Button("加载更多") { Task { await store.list(pc: pc.id, path: store.path, offset: offset) } }.disabled(!loaded)
                        }
                    }
                }
            }
        }
        .navigationTitle(pc.name).navigationBarTitleDisplayMode(.inline)
        .toolbar { Button("关闭") { dismiss() } }
        .task { await store.list(pc: pc.id, path: initialPath) }
        .sheet(item: $creatingAI, onDismiss: { if createdAI { dismiss() } }) { location in
            NavigationStack {
                AICreateView(workspace: product.ai, initialLocation: location, onCreated: { createdAI = true })
            }
        }
    }
    @ViewBuilder private func directoryActions(path: String) -> some View {
        Button("在此新建 AI 对话", systemImage: "sparkles") {
            do {
                let location = try AICreateLocation(pcID: pc.id, directory: path)
                try location.validate(in: product.pcs)
                createdAI = false; creatingAI = location
            } catch { product.report(error) }
        }.disabled(!canCreateAI)
        ForEach(pc.commands, id: \.self) { command in
            Button("在此打开 \(command["display_name"].string)") {
                Task {
                    do { try await store.openShell(pc: pc.id, path: path, command: command["id"].string); dismiss() }
                    catch { product.report(error) }
                }
            }
        }
        Button("下载目录 ZIP", systemImage: "arrow.down.doc") {
            let name = path.replacingOccurrences(of: "\\", with: "/").split(separator: "/").last.map(String.init) ?? "directory"
            download(RemoteEntry(raw: .object(["path": .string(path), "name": .string(name.replacingOccurrences(of: ":", with: "")), "kind": .string("directory")])))
        }
    }
    private func navigate(_ path: String) { Task { await store.list(pc: pc.id, path: path) } }
    private func download(_ entry: RemoteEntry) {
        Task { do { try await store.download(entry, pc: pc) } catch { product.report(error) } }
    }
}
struct DownloadsView:View {
    @ObservedObject var store:RemoteFileStore
    @Environment(\.dismiss) private var dismiss
    var body:some View{List{ForEach(store.downloads){record in VStack(alignment:.leading,spacing:7){Text(record.filename).font(.headline);Text(record.status).font(.caption);if record.expectedBytes>0{ProgressView(value:Double(record.received),total:Double(record.expectedBytes))};if let error=record.error{Text(error).font(.caption).foregroundStyle(.orange)};HStack{if record.status=="COMPLETED",let url=try? store.localFile(record){ShareLink(item:url){Label("导出到文件",systemImage:"square.and.arrow.up")}}else{Button("重试"){store.retry(record.id)};Button("取消"){store.cancel(record.id)}};Button("移除记录",role:.destructive){store.removeRecord(record.id)}}}}}.navigationTitle("下载").toolbar{Button("关闭"){dismiss()}}}
}
