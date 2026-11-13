// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI

@MainActor final class CLIDirectoryBrowser: ObservableObject {
    typealias Load = (String, Int64, String) async throws -> CLIDirectoryPage
    @Published private(set) var page: CLIDirectoryPage?
    @Published private(set) var busy = false
    @Published private(set) var error: String?
    @Published var query = ""
    private let load: Load
    private var generation = UUID(), loadedQuery = ""
    init(load: @escaping Load) { self.load=load }
    func cancel() {generation=UUID();busy=false}
    func browse(_ path: String, more: Bool = false) async {
        guard !busy else{return};let owner=generation;busy=true;error=nil;defer{if owner==generation{busy=false}}
        let offset=more ? page?.nextOffset ?? 0 : 0
        let filter=more ? loadedQuery:query
        do {
            let next=try await load(path,offset,filter)
            guard owner==generation,!Task.isCancelled else{return}
            if more,let current=page {
                guard current.path==next.path else{throw ClientError.protocolViolation("directory page changed")}
                var seen=Set<String>();let joined=(current.entries+next.entries).filter{seen.insert($0.id).inserted}
                guard joined.count<=10000 else{throw ClientError.limitExceeded}
                page = .init(path:next.path,parent:next.parent,entries:joined,nextOffset:next.nextOffset)
            } else {page=next;loadedQuery=filter}
        } catch {if owner==generation {self.error=error.localizedDescription}}
    }
}
struct CLIDirectoryPicker: View {
    @StateObject private var browser: CLIDirectoryBrowser
    let initialPath: String, selected: (String) -> Void
    @Environment(\.dismiss) private var dismiss
    init(initialPath: String, load: @escaping CLIDirectoryBrowser.Load, selected: @escaping (String) -> Void) {
        self.initialPath=initialPath;self.selected=selected;_browser=StateObject(wrappedValue:CLIDirectoryBrowser(load:load))
    }
    var body: some View {
        List {
            Section("当前位置") {
                Text(browser.page?.path.isEmpty == false ? browser.page!.path : "电脑上的可用位置")
                    .font(.caption).textSelection(.enabled).accessibilityIdentifier("cli-directory-current")
                HStack {
                    Button("可用位置",systemImage:"internaldrive"){browser.query="";Task{await browser.browse("")}}.disabled(browser.busy)
                    if let parent=browser.page?.parent {Button("上一级",systemImage:"arrow.up"){Task{await browser.browse(parent)}}.disabled(browser.busy)}
                }
                TextField("筛选当前目录",text:$browser.query).textInputAutocapitalization(.never).autocorrectionDisabled()
                    .onSubmit{Task{await browser.browse(browser.page?.path ?? "")}}
                Button("查找目录"){Task{await browser.browse(browser.page?.path ?? "")}}.disabled(browser.busy)
            }
            if browser.busy {ProgressView("正在查询电脑目录…")}
            if let error=browser.error {
                Text(error).foregroundStyle(.orange)
                Button("重新查询目录"){Task{await browser.browse(browser.page?.path ?? initialPath)}}.disabled(browser.busy)
            }
            if let page=browser.page {
                Section("目录") {
                    ForEach(page.entries){entry in
                        Button{browser.query="";Task{await browser.browse(entry.id)}}label:{Label(entry.name,systemImage:"folder")}
                            .disabled(browser.busy).accessibilityIdentifier("cli-directory-"+entry.id)
                    }
                    if page.entries.isEmpty && !browser.busy {Text("此页没有子目录，仍可选择当前位置。").foregroundStyle(.secondary)}
                    if page.nextOffset != nil {Button("加载更多目录"){Task{await browser.browse(page.path,more:true)}}.disabled(browser.busy)}
                }
            }
        }
        .navigationTitle("选择工作目录").navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement:.cancellationAction){Button("取消"){dismiss()}}
            ToolbarItem(placement:.confirmationAction){Button("使用此目录"){
                guard let path=browser.page?.path,!path.isEmpty else{return};selected(path);dismiss()
            }.disabled(browser.busy || browser.error != nil || browser.page?.path.isEmpty != false)}
        }
        .task{await browser.browse(initialPath)}.onDisappear{browser.cancel()}
    }
}
struct CLICreationForm: View {
    let pc: PC
    @ObservedObject var store: CLICreationStore
    let directoryPage: CLIDirectoryBrowser.Load, opened: (UUID) async -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var directory=""
    @State private var command=""
    @State private var browse=false
    @State private var localError:String?
    private var current: CLICreationRequest? {store.pcID==pc.id ? store.request : nil}
    private var matchingRecent: [CLIRecentDirectory] { store.pcID == pc.id ? store.recent : [] }
    private var locked: Bool {store.busy || current != nil}
    var body: some View {
        Form {
            Section("电脑") {Text(pc.name)}
            Section("工作目录") {
                TextField("电脑上的绝对目录",text:$directory,axis:.vertical).textInputAutocapitalization(.never).autocorrectionDisabled()
                    .disabled(locked).accessibilityIdentifier("cli-working-directory")
                if pc.canBrowseFiles {Button("浏览电脑目录",systemImage:"folder"){browse=true}.disabled(locked)}
                else {Text("此电脑未提供目录浏览，可手动输入绝对路径。无需登录 AI 模型账号。").font(.caption).foregroundStyle(.secondary)}
                if !directory.isEmpty {Text(directory).font(.caption).textSelection(.enabled).accessibilityIdentifier("cli-selected-directory")}
                if !matchingRecent.isEmpty {
                    Menu("最近使用的目录") {ForEach(matchingRecent){item in Button(item.path){directory=item.path}}}.disabled(locked)
                } else if directory.isEmpty {
                    Text("请选择电脑返回的位置或填写绝对路径；不会猜测电脑用户名或主目录。").font(.caption).foregroundStyle(.secondary)
                }
            }
            Section("终端命令") {
                Picker("Shell",selection:$command){ForEach(pc.commands,id:\.self){c in Text(c["display_name"].string).tag(c["id"].string)}}.disabled(locked)
            }
            if let request=current {
                Section("创建回执") {
                    Text(store.statusText).accessibilityIdentifier("cli-receipt-state")
                    LabeledContent("原工作目录",value:request.path)
                    LabeledContent("原终端命令",value:request.command["display_name"].string.isEmpty ? request.commandID:request.command["display_name"].string)
                    Text("请求编号："+request.idempotency.uuidString).font(.caption2).textSelection(.enabled)
                    if let operation=request.operationID {Text("电脑任务："+operation.uuidString).font(.caption2).textSelection(.enabled)}
                    if let failure=request.failure {Text(failure).foregroundStyle(.orange)}
                    if let session=request.sessionID {
                        Button("打开已创建的会话"){Task{await opened(session);dismiss()}}.disabled(store.busy)
                    } else if !request.complete {
                        Button(request.operationID == nil ? "继续原请求并查询回执":"查询原请求回执") {Task{await recover()}}
                            .disabled(store.busy).accessibilityIdentifier("cli-recover-request")
                    }
                    if request.complete {Button("准备新建另一会话"){do{try store.clearCompleted(pc:pc.id);syncInputs()}catch{localError=error.localizedDescription}}.disabled(store.busy)}
                    else {Text("关闭窗口不会取消电脑任务。重新进入可查询原回执；未确认前不会创建新的请求。").font(.caption).foregroundStyle(.secondary)}
                }
            } else {
                Button("创建会话"){Task{await create()}}
                    .disabled(store.busy || command.isEmpty || (try? CLIPath.validate(directory,platform:pc.platform)) == nil || !pc.canCreateTerminal)
                    .accessibilityIdentifier("cli-create-submit")
            }
            if store.busy {ProgressView("正在确认电脑回执…")}
            if let error=localError ?? store.error {Text(error).foregroundStyle(.orange).accessibilityIdentifier("cli-create-error")}
        }
        .navigationTitle("新建 CLI 会话").navigationBarTitleDisplayMode(.inline)
        .toolbar{ToolbarItem(placement:.cancellationAction){Button("关闭"){dismiss()}}}
        .task{store.show(pc:pc.id);syncInputs()}
        .sheet(isPresented:$browse){NavigationStack{CLIDirectoryPicker(initialPath:directory,load:directoryPage){directory=$0}}}
    }
    private func syncInputs(){directory=current?.path ?? matchingRecent.first?.path ?? "";command=current?.commandID ?? pc.commands.first?["id"].string ?? ""}
    private func create() async {localError=nil;do{if let id=try await store.create(pc:pc.id,path:directory,commandID:command){await opened(id);dismiss()}}catch{localError=error.localizedDescription}}
    private func recover() async {localError=nil;do{if let id=try await store.recover(pc:pc.id){await opened(id);dismiss()}}catch{localError=error.localizedDescription}}
}
