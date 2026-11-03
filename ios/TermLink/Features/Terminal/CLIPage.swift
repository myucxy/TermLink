// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI

struct CLIPage:View {
    @EnvironmentObject var product:ProductStore
    @AppStorage("terminal-font-size") private var fontSize=14.0
    var body:some View{GeometryReader{geometry in
        if geometry.size.width>=850{HStack(spacing:0){list.frame(width:280);Divider();detail}}
        else{VStack(spacing:0){ScrollView(.horizontal){HStack{ForEach(Array(product.terminals.values),id:\.session.id){t in HStack{Button(t.session.title.isEmpty ? t.session.shell:t.session.title){product.selectedCLI=t.session.id};Button{product.closeView(t.session.id)}label:{Image(systemName:"xmark").font(.caption)}}.padding(9).background(product.selectedCLI==t.session.id ? Color.accentColor.opacity(0.2):Color.clear,in:RoundedRectangle(cornerRadius:8))}}.padding(.horizontal,8)};detail}}
    }.navigationTitle("CLI").navigationBarTitleDisplayMode(.inline)}
    private var list:some View{List(Array(product.terminals.values),id:\.session.id){t in Button{product.selectedCLI=t.session.id}label:{VStack(alignment:.leading){Text(t.session.title.isEmpty ? t.session.shell:t.session.title);Text(t.status).font(.caption).foregroundStyle(.secondary)}}.contextMenu{Button("关闭视图"){product.closeView(t.session.id)}}}}
    @ViewBuilder private var detail:some View{if let id=product.selectedCLI,let t=product.terminals[id]{TerminalDetailView(controller:t,fontSize:fontSize)}else{ContentUnavailableView("打开 CLI 会话",systemImage:"terminal",description:Text("从设备页面选择电脑上的会话。"))}}
}
struct TerminalDetailView:View {
    @ObservedObject var controller:TerminalController
    let fontSize:Double
    @EnvironmentObject var product:ProductStore
    @EnvironmentObject var shortcuts:TerminalShortcutStore
    @State private var confirmEnd = false
    @State private var showPaste = false
    @State private var paste = ""
    @State private var resizeBusy = false
    @State private var resizeFeedback: String?
    @State private var shellCommands: TerminalShellState?
    @State private var directoryPath: String?
    var body:some View{VStack(spacing:0){
        HStack(spacing:6){Circle().fill(controller.error==nil ? .green:.orange).frame(width:7,height:7);Text(controller.session.title.isEmpty ? controller.session.shell:controller.session.title).font(.subheadline).lineLimit(1);Spacer();Menu{
            Button(controller.canInput ? "释放控制权":"请求控制权"){Task{do{if controller.canInput{try await controller.releaseControl()}else{try await controller.requestControl()}}catch{product.report(error)}}}.disabled(controller.frozen)
            Section("历史与目录") {
                Button("浏览滚回历史") { controller.browseHistory(by: max(1, (controller.frame?.rows ?? 24) - 1)) }.disabled(controller.frame == nil)
                Button("打开当前目录") {
                    do {
                        let state = try controller.shellState()
                        guard let path = TerminalShellState.localWindowsDirectory(state.currentDirectoryURI) else {
                            throw ClientError.unavailable("Shell 尚未报告可打开的本地电脑目录。")
                        }
                        directoryPath = path
                    } catch { product.report(error) }
                }.disabled(controller.frame == nil)
                Button("Shell 命令记录") {
                    do { shellCommands = try controller.shellState() } catch { product.report(error) }
                }.disabled(controller.frame == nil)
            }
            Divider()
            Section("窗口尺寸"){
                if let frame=controller.frame{Button("\(frame.columns) 列 × \(frame.rows) 行"){}.disabled(true)}
                Button("按手机窗口调整会话"){resizeWindow{try await controller.fitToPhone()}}.disabled(resizeBusy)
                Button("恢复电脑窗口尺寸"){resizeWindow{try await controller.restorePCWindow()}}.disabled(resizeBusy)
            }
            Divider()
            Button("粘贴"){showPaste=true}
            Button("重新打开并确认状态"){Task{await product.open(controller.session)}}
            Button("结束电脑上的会话",role:.destructive){confirmEnd=true}
        }label:{HStack(spacing:4){Text(controller.status).font(.caption);Image(systemName:"cloud.fill")}.foregroundStyle(.secondary)}}.padding(.horizontal,8).frame(minHeight:34)
        if let resizeFeedback{Text(resizeFeedback).font(.caption2).foregroundStyle(.secondary).frame(maxWidth:.infinity,alignment:.leading).padding(.horizontal,8)}
        if let error=controller.error{Text(error).font(.caption).foregroundStyle(.orange).padding(8)}
        if controller.frozen {
            HStack {
                Button("上一屏", systemImage: "chevron.up") { controller.browseHistory(by: max(1, (controller.frame?.rows ?? 24) - 1)) }
                    .labelStyle(.iconOnly).frame(minWidth:32,minHeight:32)
                    .disabled(controller.presentation.offset >= controller.presentation.availableRows)
                Text("历史 · \(controller.presentation.offset)/\(controller.presentation.availableRows) 行").font(.caption).lineLimit(1)
                Button("下一屏", systemImage: "chevron.down") { controller.browseHistory(by: -max(1, (controller.frame?.rows ?? 24) - 1)) }
                    .labelStyle(.iconOnly).frame(minWidth:32,minHeight:32)
                    .disabled(controller.presentation.offset == 0)
                Spacer()
                Button("返回实时尾部") { controller.endHistory() }
            }.font(.caption).padding(8)
        }
        TerminalSurface(controller:controller,fontSize:fontSize)
        shortcutDock.padding(4).background(.black)
    }.background(Color(red:8/255,green:12/255,blue:16/255))
    .confirmationDialog("结束电脑上的会话？",isPresented:$confirmEnd){Button("结束 SessionHost 与 Shell",role:.destructive){Task{do{try await controller.terminate();product.closeView(controller.session.id)}catch{product.report(error)}}}}message:{Text("这会结束对应电脑进程，关闭视图不会执行此操作。")}
    .sheet(isPresented:$showPaste){NavigationStack{Form{TextEditor(text:$paste).frame(minHeight:180);PasteButton(payloadType:String.self){items in paste=items.joined(separator:"\n")};Button("粘贴到当前终端"){Task{do{try await controller.paste(Data(paste.utf8));showPaste=false;paste=""}catch{product.report(error)}}}.disabled(!controller.canInput)}.navigationTitle("确认粘贴").toolbar{Button("取消"){showPaste=false}}}}
    .sheet(isPresented:Binding(get:{shellCommands != nil},set:{if !$0{shellCommands=nil}})) {
        if let shellCommands {
            NavigationStack {
                List {
                    if shellCommands.commands.isEmpty { Text("Shell 尚未报告命令记录。") }
                    ForEach(shellCommands.commands.reversed()) { command in
                        Button {
                            do { try controller.showShellCommand(command); self.shellCommands = nil }
                            catch { product.report(ClientError.unavailable("此命令的位置已不在保留历史中。")) }
                        } label: {
                            VStack(alignment:.leading,spacing:4) {
                                Text("#\(command.id) · \(command.phaseLabel)")
                                Text(TerminalShellState.localWindowsDirectory(command.currentDirectoryURI) ?? command.currentDirectoryURI)
                                    .font(.caption).foregroundStyle(.secondary).lineLimit(2)
                            }
                        }.disabled(command.prompt.alternate || command.prompt.page != 1)
                    }
                }.navigationTitle("Shell 命令记录").toolbar { Button("关闭") { self.shellCommands = nil } }
            }
        }
    }
    .sheet(isPresented:Binding(get:{directoryPath != nil},set:{if !$0{directoryPath=nil}})) {
        if let directoryPath,let pc=product.pcs.first(where:{$0.id==controller.session.pcID}) {
            NavigationStack { RemoteFilesView(store:product.files,pc:pc,initialPath:directoryPath) }
        }
    }
    }
    private func resizeWindow(_ action:@escaping () async throws ->(columns:Int,rows:Int)){
        resizeBusy=true;resizeFeedback="正在等待 Windows 应用尺寸…"
        Task{do{let applied=try await action();resizeFeedback="Windows 已应用 \(applied.columns) 列 × \(applied.rows) 行。"}catch{resizeFeedback=error.localizedDescription};resizeBusy=false}
    }
    private enum DockItem { case keyboard,paste,key(String,UInt32),custom(TerminalCustomShortcut)
        var id:String{switch self{case .keyboard:return"builtin:keyboard";case .paste:return"builtin:paste";case .key(let label,_):return"builtin:\(label)";case .custom(let s):return"custom:\(s.id)"}}}
    @ViewBuilder private var shortcutDock:some View{
        if shortcuts.twoRows{
            let rows=TerminalShortcutLayout.balancedRows(shortcuts.customs)
            let top:[DockItem]=[.keyboard,.key("Esc",5),.key("⌫",3),.key("↑",12),.key("Del",6),.key("Ctrl+C",1)]+rows.top.map(DockItem.custom)
            let bottom:[DockItem]=[.paste,.key("Tab",4),.key("←",14),.key("↓",13),.key("→",15),.key("Enter",2)]+rows.bottom.map(DockItem.custom)
            VStack(spacing:3){dock(top);dock(bottom)}
        }else{
            let single:[DockItem]=[.keyboard,.key("Esc",5),.key("Tab",4),.key("Enter",2),.key("⌫",3),.key("Del",6),.key("←",14),.key("↑",12),.key("↓",13),.key("→",15),.key("Ctrl+C",1),.paste]+shortcuts.customs.map(DockItem.custom)
            dock(single)
        }
    }
    private func dock(_ values:[DockItem])->some View{ScrollView(.horizontal){HStack(spacing:3){ForEach(values,id:\.id){item in
        Button{switch item{
            case .paste:showPaste=true
            case .keyboard:NotificationCenter.default.post(name:.terminalShowKeyboard,object:controller.session.id)
            case .key("Ctrl+C",_):Task{do{try await controller.text("\u{3}")}catch{product.report(error)}}
            case .key(_,let code):Task{do{try await controller.key(code)}catch{product.report(error)}}
            case .custom(let shortcut):Task{do{try await controller.dispatch(shortcut)}catch{product.report(error)}}
        }}label:{switch item{
            case .keyboard:Image(systemName:"keyboard").frame(width:24,height:30)
            case .paste:Text("粘贴").font(.system(size:13,design:.monospaced)).frame(minWidth:24,minHeight:30)
            case .key(let label,_):Text(label).font(.system(size:13,design:.monospaced)).frame(minWidth:24,minHeight:30)
            case .custom(let shortcut):Text(shortcut.displayLabel).font(.system(size:13,design:.monospaced)).lineLimit(1).frame(minWidth:24,maxWidth:72,minHeight:30)
        }}.padding(.horizontal,7).foregroundStyle(.secondary).background(Color.white.opacity(0.04),in:RoundedRectangle(cornerRadius:5)).overlay{RoundedRectangle(cornerRadius:5).stroke(.gray.opacity(0.5))}.disabled(!controller.canInput)
    }}}.scrollIndicators(.hidden)}
}
private extension TerminalShellCommand {
    var phaseLabel: String {
        switch phase {
        case .prompt, .input: return "输入中"
        case .output: return "运行中"
        case .completed: return exitCode.map { "退出码 \($0)" } ?? "已结束"
        case .interrupted: return "已中断"
        }
    }
}
extension Notification.Name{static let terminalShowKeyboard=Notification.Name("TermLinkTerminalShowKeyboard")}
