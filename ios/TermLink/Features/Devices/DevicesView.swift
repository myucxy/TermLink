// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI

struct DevicesView:View {
    @EnvironmentObject var product:ProductStore
    @State private var filesPC:PC?
    @State private var createPC:PC?
    @State private var updatePC:PC?
    @State private var showDownloads=false
    @State private var showActivity=false
    @State private var ending:TerminalSession?
    var body:some View{ScrollView{LazyVStack(spacing:12){
        if !product.signedIn{ContentUnavailableView("登录 TermLink",systemImage:"laptopcomputer.and.iphone",description:Text("使用同一账号连接电脑，访问 CLI、AI 和文件。"));Button("授权登录"){Task{await product.signIn()}}.buttonStyle(.borderedProminent)}
        ForEach(product.pcs){pc in VStack(alignment:.leading,spacing:10){
            HStack{Image(systemName:"laptopcomputer");Text(pc.name).font(.headline);Circle().fill(pc.online ? .green:.gray).frame(width:6,height:6);Spacer();Menu{Button("浏览文件"){filesPC=pc};Button("新建 CLI 会话"){createPC=pc};if pc.platform=="windows"{Button("升级 Windows 客户端"){updatePC=pc}}}label:{Image(systemName:"ellipsis").frame(width:44,height:44)}}
            AgentVersionRow(updates:product.updates,pc:pc,onUpdate:{updatePC=pc})
            AgentUpdateCard(updates:product.updates,pc:pc)
            HStack{Button("CLI",systemImage:"terminal"){createPC=pc};Button("AI",systemImage:"sparkles"){product.tab = .ai;Task{await product.ai.refreshAll()}};Button("文件",systemImage:"folder"){filesPC=pc}}.buttonStyle(.bordered).disabled(!pc.online || !pc.routing)
            ForEach(product.sessions.filter{$0.pcID==pc.id}){s in HStack{Button{Task{await product.open(s)}}label:{Label(s.title.isEmpty ? s.shell:s.title,systemImage:"terminal").frame(maxWidth:.infinity,alignment:.leading)}.disabled(product.catalogErrors[pc.id] != nil || !pc.online);Text(s.lifecycle).font(.caption2).foregroundStyle(.secondary)}.padding(.vertical,4).contextMenu{Button("结束电脑上的会话",role:.destructive){ending=s}}}
            if product.catalogErrors[pc.id] != nil{HStack{Text("状态待确认").font(.caption);Spacer();Button("重新确认"){Task{await product.refreshCatalogs()}}}}
        }.padding(14).background(.background,in:RoundedRectangle(cornerRadius:12)).overlay{RoundedRectangle(cornerRadius:12).stroke(.secondary.opacity(0.25))}}
    }.padding()}.navigationTitle("你的终端").refreshable{do{try await product.refresh();await product.refreshCatalogs()}catch{product.report(error)}}
    .toolbar{Button("活动",systemImage:"clock.arrow.circlepath"){showActivity=true};Button("下载",systemImage:"arrow.down.circle"){showDownloads=true}}
    .sheet(item:$filesPC){pc in NavigationStack{RemoteFilesView(store:product.files,pc:pc)}}
    .sheet(item:$createPC){pc in NavigationStack{CreateTerminalView(pc:pc)}}
    .sheet(item:$updatePC){pc in AgentUpdateSheet(updates:product.updates,pc:pc)}
    .sheet(isPresented:$showDownloads){NavigationStack{DownloadsView(store:product.files)}}
    .sheet(isPresented:$showActivity){NavigationStack{ActivityView()}}
    .confirmationDialog("结束电脑上的终端进程？",isPresented:Binding(get:{ending != nil},set:{if !$0{ending=nil}})){Button("结束 SessionHost 与 Shell",role:.destructive){guard let session=ending else{return};ending=nil;Task{do{try await product.terminate(session)}catch{product.report(error)}}}}}
}
struct CreateTerminalView:View {
    let pc:PC
    @EnvironmentObject var product:ProductStore
    @Environment(\.dismiss) private var dismiss
    @State private var directory=""
    @State private var command=""
    @State private var busy=false
    var body:some View{Form{TextField("工作目录（电脑端默认目录留空）",text:$directory).textInputAutocapitalization(.never);Picker("终端命令",selection:$command){ForEach(pc.commands,id:\.self){c in Text(c["display_name"].string).tag(c["id"].string)}};Button("创建会话"){busy=true;Task{defer{busy=false};do{try await product.files.openShell(pc:pc.id,path:directory,command:command);dismiss()}catch{product.report(error)}}}.disabled(busy || command.isEmpty)}.navigationTitle("新建 CLI 会话").toolbar{Button("取消"){dismiss()}}.onAppear{command=pc.commands.first?["id"].string ?? ""}}
}
struct AgentVersionRow:View {
    @ObservedObject var updates:AgentUpdateStore
    let pc:PC
    let onUpdate:()->Void
    var body:some View{HStack(spacing:4){Text("Agent \(pc.version)").lineLimit(1)
        if pc.online,pc.routing,updates.offers[pc.id]?["eligible"].bool==true{
            Button(action:onUpdate){Image(systemName:"arrow.up.right").font(.caption.weight(.semibold)).foregroundStyle(.green)}.buttonStyle(.plain).accessibilityLabel("Windows 客户端有更新")
        }
        Text("· \(pc.platform) · \(pc.routing ? "远程路由":"远程访问关闭")").lineLimit(1)
    }.font(.caption).foregroundStyle(.secondary).frame(minHeight:16)}
}
struct AgentUpdateCard:View {
    @ObservedObject var updates:AgentUpdateStore
    let pc:PC
    var body:some View{if let task=updates.tasks[pc.id]{VStack(alignment:.leading){HStack{Text(task.terminal ? task.status:"正在升级 · \(task.phase)").font(.caption);Spacer();if task.terminal{Button("查看结果"){updates.selected=pc.id};Button("关闭提示",systemImage:"xmark"){updates.dismiss(pc.id)}}};if !task.terminal{ProgressView(value:task.percent)}}.sheet(isPresented:Binding(get:{updates.selected==pc.id},set:{if !$0{updates.minimize()}})){AgentUpdateSheet(updates:updates,pc:pc)}}else if let error=updates.visibleError(pc.id){HStack{Text(error).font(.caption).foregroundStyle(.orange);Spacer();Button("关闭",systemImage:"xmark"){updates.dismiss(pc.id)}}}}
}
struct AgentUpdateSheet:View {
    @ObservedObject var updates:AgentUpdateStore
    let pc:PC
    @Environment(\.dismiss) private var dismiss
    @State private var eligibility:JSONValue?
    @State private var error:String?
    var body:some View{NavigationStack{Form{Text(pc.name);if let task=updates.tasks[pc.id]{Text(task.status);Text(task.phase);ProgressView(value:task.percent);if task.cancelable{Button("取消升级",role:.destructive){Task{do{try await updates.cancel(pc.id)}catch{self.error=error.localizedDescription}}}}}else if let value=eligibility{Text("当前 \(pc.version) → \(value["target_version"].string)");if value["eligible"].bool{Button("确认升级"){dismiss();Task{await updates.install(pc.id,target:value["target_build"].integer)}}}else{Text(value["reason_code"].string)}}else{ProgressView()};if let error{Text(error).foregroundStyle(.red)}}.navigationTitle("升级 Windows 客户端").toolbar{Button("收起"){updates.minimize();dismiss()}}.task{do{eligibility=try await updates.eligibility(pc.id)}catch{self.error=error.localizedDescription}}}}
}
