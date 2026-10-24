// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI
import PhotosUI
import AVFoundation
import UniformTypeIdentifiers

struct AIPage:View {
    @ObservedObject var workspace:AIWorkspace
    @EnvironmentObject var product:ProductStore
    @State private var create=false,history=false
    var body:some View{Group{if let id=workspace.selected,let session=workspace.conversations[id],!workspace.unavailable.contains(session.pcID){AIConversationView(workspace:workspace,session:session)}else{List{
        ForEach(product.pcs.filter{$0.capabilities.contains("ai_agent_v1")}){pc in Section(pc.name){
            ForEach(workspace.conversations.values.filter{$0.pcID==pc.id && (!workspace.attentionOnly || !$0.pending.isEmpty) && (workspace.search.isEmpty || ($0.title+$0.directory+$0.agent).localizedCaseInsensitiveContains(workspace.search))}.sorted{a,b in let ap=workspace.pinned.contains(a.id),bp=workspace.pinned.contains(b.id);return ap != bp ? ap:a.title<b.title}){session in
                Button{workspace.open(session)}label:{HStack{AgentIcon(agent:session.raw["agentId"].string);VStack(alignment:.leading){Text(session.title);Text(session.agent+" · "+session.state).font(.caption).foregroundStyle(.secondary)};Spacer();if !session.pending.isEmpty{Image(systemName:"exclamationmark.bubble").foregroundStyle(.orange)}}}.disabled(workspace.unavailable.contains(pc.id))
            }
            if workspace.catalogs[pc.id]?["agents"].array.contains(where:{$0["id"].string=="zcode" && $0["strategy"].string=="desktop_remote" && AgentBrand.available($0)})==true{
                NavigationLink{AIRemoteView(workspace:workspace,pc:pc.id)}label:{Label{Text("ZCode · 桌面远控")}icon:{AgentIcon(agent:"zcode")}}.disabled(workspace.unavailable.contains(pc.id))
            }
        }}
        if workspace.catalogs.isEmpty{ContentUnavailableView("开始一段 AI 对话",systemImage:"sparkles",description:Text("从在线电脑中选择已检测的 AI Agent。"))}
    }.searchable(text:$workspace.search).refreshable{await workspace.refreshAll()}.toolbar{Button(workspace.attentionOnly ? "全部":"待处理",systemImage:"exclamationmark.bubble"){workspace.attentionOnly.toggle()};Button("历史",systemImage:"clock"){history=true};Button("新建",systemImage:"plus"){create=true}}}}
    .navigationTitle("AI").navigationBarTitleDisplayMode(.inline).task{await workspace.refreshAll()}
    .sheet(isPresented:$create){NavigationStack{AICreateView(workspace:workspace)}}
    .sheet(isPresented:$history){NavigationStack{AINativeHistoryView(workspace:workspace)}}}
}
struct AgentIcon:View {
    let agent:String
    private var name:String{AgentBrand.asset(agent) ?? ""}
    var body:some View{if name.isEmpty{Image(systemName:"bubble.left.and.bubble.right").frame(width:22,height:22)}else{Image(name).resizable().scaledToFit().frame(width:22,height:22).foregroundStyle(.primary)}}
}
struct AIConversationView:View {
    @ObservedObject var workspace:AIWorkspace
    let session:AIConversation
    @EnvironmentObject var product:ProductStore
    @State private var showFiles=false,showModels=false,confirmEnd=false,rename=false,title="",confirmForget=false
    @State private var showPhotos=false,showCamera=false,showComputerFiles=false,photo:PhotosPickerItem?
    @State private var pendingMode:JSONValue?
    private var blocked:Bool{!session.canControl || workspace.busy.contains(session.pcID) || workspace.uncertain[session.pcID] != nil}
    var body:some View{VStack(spacing:0){HStack{Button{workspace.selected=nil}label:{Image(systemName:"arrow.left").frame(width:36,height:36)};VStack(alignment:.leading,spacing:1){Text(session.title).font(.subheadline).lineLimit(1);Text(session.agent+" · "+(product.pcs.first{$0.id==session.pcID}?.name ?? "电脑")+" · "+session.directory).font(.caption2).foregroundStyle(.secondary).lineLimit(1)};Spacer();Menu{
        Button("重新确认连接"){Task{do{try await workspace.refresh(session.pcID,session:session.id);_=try await workspace.mutate(pc:session.pcID,action:"control",session:session)}catch{product.report(error)}}}
        if session.capabilities["resume"].bool{Button("恢复会话"){action("resume")}}
        if session.capabilities["fork"].bool{Button("分叉会话"){action("fork")}.disabled(blocked || session.state != "idle" || !session.pending.isEmpty)}
        Button("导出 Markdown"){Task{do{try await workspace.exportMarkdown(session)}catch{product.report(error)}}}.disabled(!session.capabilities["history"].bool)
        Button("重命名"){title=session.title;rename=true}.disabled(blocked)
        Button(workspace.pinned.contains(session.id) ? "取消置顶":"置顶"){workspace.togglePin(session.id)}
        if ["exited","interrupted","suspended"].contains(session.state),session.capabilities["history"].bool{Button("删除电脑历史",role:.destructive){confirmForget=true}.disabled(workspace.busy.contains(session.pcID) || workspace.uncertain[session.pcID] != nil)}
        Button("关闭视图"){workspace.closeView(session.id)};Button("结束对话",role:.destructive){confirmEnd=true}.disabled(blocked)
    }label:{Image(systemName:"cloud.fill").foregroundStyle(.secondary).frame(width:36,height:36)}}.padding(.horizontal,4)
        Divider();AIWebConversation(workspace:workspace,session:session)
        if workspace.uncertain[session.pcID] != nil{HStack{Text("请求结果待确认").font(.caption);Button("查询结果"){Task{do{try await workspace.retryUncertain(session.pcID)}catch{product.report(error)}}}}}
        VStack(spacing:0){
            ForEach(workspace.queued[session.id] ?? []){item in HStack{Text(item.restored ? "已恢复 · 请取回后发送":item.failed ? "发送待确认 · 请取回":"待发送 · "+item.text).lineLimit(1).font(.caption);Spacer();Button("取回"){do{try workspace.recoverQueued(session.id,id:item.id)}catch{product.report(error)}}}}
            ScrollView(.horizontal){HStack{ForEach(workspace.references[session.id] ?? [],id:\.self){path in HStack{Text("@"+(path as NSString).lastPathComponent);Button("移除",systemImage:"xmark"){workspace.references[session.id]?.removeAll{$0==path};workspace.persist()}}.font(.caption).padding(6).background(.quaternary,in:Capsule())}}}
            ScrollView(.horizontal){HStack{ForEach(workspace.attachments[session.id] ?? []){item in HStack{Text(item.name+" · "+(item.status=="ready" ? "就绪":item.status=="failed" ? "失败":"\(item.sent*100/max(1,item.size))%"));Button("移除",systemImage:"xmark"){Task{do{try await workspace.removeAttachment(session,id:item.id)}catch{product.report(error)}}}}.font(.caption).padding(6).background(.quaternary,in:Capsule())}}}
            TextEditor(text:Binding(get:{workspace.drafts[session.id] ?? ""},set:{workspace.drafts[session.id]=String($0.prefix(16000));workspace.persist()})).frame(minHeight:44,maxHeight:100).padding(.horizontal,6).scrollContentBackground(.hidden).disabled(blocked).accessibilityLabel("输入消息")
            HStack(spacing:4){Menu{if session.capabilities["attachments"].bool{Button("文件"){showFiles=true};Button("照片"){showPhotos=true};if UIImagePickerController.isSourceTypeAvailable(.camera){Button("拍摄照片"){Task{if await AVCaptureDevice.requestAccess(for:.video){showCamera=true}else{product.report(ClientError.unavailable("未获得相机权限，可在系统设置中允许访问。"))}}}};Button("电脑文件"){showComputerFiles=true}};if session.state=="running"{Button("加入待发队列"){do{try workspace.enqueue(session)}catch{product.report(error)}};if session.capabilities["steer"].bool{Button("补充当前任务"){action("steer",extra:["text":.string(workspace.drafts[session.id] ?? "")])}}}}label:{Image(systemName:"plus").frame(width:32,height:36)}.disabled(blocked)
                Menu{ForEach(session.capabilities["configuration"]["permissionModes"].array,id:\.self){mode in Button(mode["label"].string.isEmpty ? mode["id"].string:mode["label"].string){if mode["requiresConfirmation"] != .bool(false),mode["id"] != session.raw["permissionMode"]{pendingMode=mode}else{configure(mode)}}}}label:{Text(session.raw["permissionMode"].string.isEmpty ? "授权方式":session.raw["permissionMode"].string).font(.caption);Image(systemName:"chevron.down").font(.caption2)}.disabled(blocked || session.state != "idle" || !session.pending.isEmpty || !session.capabilities["configuration"]["permissionSwitch"].bool)
                Spacer();Button{showModels=true}label:{Text(session.raw["modelId"].string.isEmpty ? "默认模型":session.raw["modelId"].string).font(.caption).lineLimit(1);Image(systemName:"chevron.down").font(.caption2)}
                Button{Task{do{if session.state=="running"{_=try await workspace.mutate(pc:session.pcID,action:"interrupt",session:session)}else{try await workspace.send(session)}}catch{product.report(error)}}}label:{Image(systemName:session.state=="running" ? "stop.fill":"paperplane.fill").frame(width:40,height:40)}.disabled(blocked)
            }.padding(.horizontal,4)
        }.background(Color(uiColor:.secondarySystemBackground),in:RoundedRectangle(cornerRadius:12)).overlay{RoundedRectangle(cornerRadius:12).stroke(.secondary.opacity(0.3))}.padding(.horizontal,8).padding(.vertical,4)
    }.fileImporter(isPresented:$showFiles,allowedContentTypes:[.data],allowsMultipleSelection:false){result in Task{do{guard let url=try result.get().first else{return};try await workspace.upload(session,url:url)}catch{product.report(error)}}}
    .sheet(isPresented:$showModels){NavigationStack{AIModelView(workspace:workspace,session:session)}}
    .sheet(isPresented:$showComputerFiles){NavigationStack{AIComputerFilePicker(workspace:workspace,pc:session.pcID,initialPath:session.directory){path in do{try workspace.addReference(session,path:path)}catch{product.report(error)}}}}
    .photosPicker(isPresented:$showPhotos,selection:$photo,matching:.images)
    .task(id:photo){guard let photo else{return};do{if let file=try await photo.loadTransferable(type:AIPhotoFile.self){await uploadLocalPhoto(.success(file.url))}}catch{product.report(error)};self.photo=nil}
    .sheet(isPresented:$showCamera){AICameraView{result in showCamera=false;Task{await uploadLocalPhoto(result)}}.ignoresSafeArea()}
    .sheet(isPresented:Binding(get:{workspace.exportURL != nil},set:{if !$0{workspace.exportURL=nil}})){if let url=workspace.exportURL{ActivityShareView(url:url)}}
    .sheet(isPresented:Binding(get:{workspace.directoryLocation != nil},set:{if !$0{workspace.directoryLocation=nil}})){if let location=workspace.directoryLocation,let pc=product.pcs.first(where:{$0.id==location.0}){NavigationStack{RemoteFilesView(store:product.files,pc:pc,initialPath:location.1)}}}
    .alert("使用浏览器打开链接？",isPresented:Binding(get:{workspace.externalLink != nil},set:{if !$0{workspace.externalLink=nil}})){Button("取消",role:.cancel){workspace.externalLink=nil};Button("打开"){if let url=workspace.externalLink{UIApplication.shared.open(url)};workspace.externalLink=nil}}message:{Text(workspace.externalLink?.host ?? "")}
    .alert("切换 Agent 授权方式",isPresented:Binding(get:{pendingMode != nil},set:{if !$0{pendingMode=nil}})){Button("取消",role:.cancel){pendingMode=nil};Button("确认切换"){guard let mode=pendingMode else{return};pendingMode=nil;configure(mode,confirmed:true)}}message:{Text(pendingMode?["description"].string ?? "设置由 Agent 原生处理，请确认权限范围。")}
    .confirmationDialog("结束电脑上的 AI 对话？",isPresented:$confirmEnd){Button("结束对话",role:.destructive){action("end")}}
    .confirmationDialog("删除电脑上的这段 AI 历史？",isPresented:$confirmForget){Button("删除历史",role:.destructive){Task{do{try await workspace.forget(session)}catch{product.report(error)}}}}message:{Text("此操作会删除 TermLink 在电脑上保留的该会话记录。")}
    .alert("重命名对话",isPresented:$rename){TextField("名称",text:$title);Button("取消",role:.cancel){};Button("保存"){action("rename",extra:["text":.string(String(title.prefix(80)))])}}
    .task(id:session.id){while !Task.isCancelled{do{try await Task.sleep(for:.seconds(1));try await workspace.refresh(session.pcID,session:session.id)}catch is CancellationError{return}catch{workspace.markUnavailable(session.pcID);return}}}}
    private func configure(_ mode:JSONValue,confirmed:Bool=false){Task{do{try await workspace.configure(session,mode:mode["id"].string,confirmed:confirmed ? mode["id"].string:nil)}catch{product.report(error)}}}
    private func uploadLocalPhoto(_ result:Result<URL,Error>) async{do{let url=try result.get();defer{try? FileManager.default.removeItem(at:url)};try await workspace.upload(session,url:url)}catch ClientError.cancelled{}catch{product.report(error)}}
    private func action(_ action:String,extra:[String:JSONValue]=[:]){Task{do{_=try await workspace.mutate(pc:session.pcID,action:action,session:session,extra:extra)}catch{product.report(error)}}}
}
struct AICreateView:View {
    @ObservedObject var workspace:AIWorkspace
    @EnvironmentObject var product:ProductStore
    @Environment(\.dismiss) private var dismiss
    @State private var pc:UUID?,agent="",directory="",managed=false,mode="",model="",provider="",effort="",confirmed="",loading=false,browse=false
    @State private var candidate:JSONValue?,modelCatalog:JSONValue = .null
    private var agents:[JSONValue]{workspace.catalogs[pc ?? UUID()]?["agents"].array.filter{AgentBrand.available($0,nativeOnly:true)} ?? []}
    private var configuration:JSONValue{agents.first(where:{$0["id"].string==agent})?["configuration"] ?? .null}
    var body:some View{
        Form{
            Picker("电脑",selection:$pc){ForEach(product.pcs.filter{$0.online && $0.routing}){pc in Text(pc.name).tag(Optional(pc.id))}}
            Picker("AI Agent",selection:$agent){ForEach(agents,id:\.self){a in Label{Text(a["name"].string)}icon:{AgentIcon(agent:a["id"].string)}.tag(a["id"].string)}}
            Toggle("使用托管工作目录",isOn:$managed)
            if !managed{TextField("电脑工作目录",text:$directory).textInputAutocapitalization(.never).autocorrectionDisabled()}
            if !managed,pc != nil{Button("浏览电脑目录"){browse=true}}
            if configuration["permissionSwitch"].bool{
                Menu{ForEach(configuration["permissionModes"].array,id:\.self){option in Button(option["label"].string.isEmpty ? option["id"].string:option["label"].string){if option["requiresConfirmation"] != .bool(false){candidate=option}else{mode=option["id"].string;confirmed=""}}}}label:{LabeledContent("授权方式",value:mode.isEmpty ? "Agent 默认":mode)}
            }
            if configuration["modelListing"].bool{
                Button(loading ? "检测中…":"检测模型"){loadModels()}.disabled(loading || agent.isEmpty)
                Picker("模型",selection:Binding<JSONValue>(get:{modelCatalog["models"].array.first(where:{$0["modelId"].string==model && $0["providerId"].string==provider}) ?? .null},set:{value in model=value["modelId"].string;provider=value["providerId"].string;effort=""})){
                    Text("Agent 默认").tag(JSONValue.null);ForEach(modelCatalog["models"].array,id:\.self){item in Text(item["name"].string.isEmpty ? item["modelId"].string:item["name"].string).tag(item)}
                }
            }
            if !reasoning["options"].array.isEmpty{
                Picker("推理强度",selection:$effort){Text("默认").tag("");ForEach(reasoning["options"].array,id:\.self){item in Text(item["value"].string).tag(item["value"].string)}}
            }
            if let pc,workspace.uncertain[pc] != nil{Button("查询上次创建结果"){Task{do{try await workspace.retryUncertain(pc);dismiss()}catch{product.report(error)}}}}
            Button("创建对话"){guard let pc else{return};Task{do{
                var options:[String:JSONValue]=["directoryMode":.string(managed ? "managed":"custom"),"permissionMode":.string(mode),"modelId":.string(model),"providerId":.string(provider),"reasoningEffort":.string(effort)]
                if !confirmed.isEmpty{options["confirmedPermissionMode"] = .string(confirmed)}
                try await workspace.create(pc:pc,agent:agent,directory:managed ? "":directory.trimmingCharacters(in:.whitespacesAndNewlines),options:options);dismiss()
            }catch{product.report(error)}}}.disabled(pc==nil || agent.isEmpty || (!managed && directory.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty) || pc.map{workspace.busy.contains($0) || workspace.uncertain[$0] != nil}==true)
        }.navigationTitle("新建 AI 对话").toolbar{Button("取消"){dismiss()}}
        .onAppear{pc=product.pcs.first{$0.online && $0.routing}?.id;selectPC()}.onChange(of:pc){_,_ in selectPC()}.onChange(of:agent){_,_ in resetOptions()}
        .sheet(isPresented:$browse){if let pc{NavigationStack{AIComputerFilePicker(workspace:workspace,pc:pc,initialPath:directory,selectFile:false){directory=$0}}}}
        .alert("确认 Agent 授权范围",isPresented:Binding(get:{candidate != nil},set:{if !$0{candidate=nil}})){
            Button("取消",role:.cancel){candidate=nil};Button("确认使用"){guard let option=candidate else{return};mode=option["id"].string;confirmed=mode;candidate=nil}
        }message:{Text(candidate?["description"].string ?? "请确认 Agent 原生权限设置。")}
    }
    private var reasoning:JSONValue{let selected=modelCatalog["models"].array.first(where:{$0["modelId"].string==model && $0["providerId"].string==provider})?["reasoning"] ?? .null;return selected == .null ? modelCatalog["reasoning"]:selected}
    private func selectPC(){agent=agents.first?["id"].string ?? "";directory=workspace.catalogs[pc ?? UUID()]?["defaultDirectory"].string ?? "";managed=workspace.catalogs[pc ?? UUID()]?["directoryMode"].string=="managed";resetOptions()}
    private func resetOptions(){mode="";confirmed="";model="";provider="";effort="";modelCatalog = .null}
    private func loadModels(){guard let pc else{return};let requested=agent;loading=true;Task{defer{loading=false};do{let value=try await workspace.modelCatalog(pc:pc,agent:requested);guard self.pc==pc,agent==requested else{return};modelCatalog=value}catch{product.report(error)}}}
}
struct AINativeHistoryView:View {
    @ObservedObject var workspace:AIWorkspace
    @EnvironmentObject var product:ProductStore
    @Environment(\.dismiss) private var dismiss
    @State private var pc:UUID?,agent="",entries:[JSONValue]=[],cursor="",loading=false
    private var agents:[JSONValue]{workspace.catalogs[pc ?? UUID()]?["agents"].array.filter{$0["nativeHistory"].bool && AgentBrand.available($0,nativeOnly:true)} ?? []}
    var body:some View{Form{Picker("电脑",selection:$pc){ForEach(product.pcs.filter{$0.online}){p in Text(p.name).tag(Optional(p.id))}};Picker("Agent",selection:$agent){ForEach(agents,id:\.self){a in Text(a["name"].string).tag(a["id"].string)}};Text("读取已选电脑与 Agent 的本机历史。继续对话会创建 TermLink 可管理的会话。").font(.caption).foregroundStyle(.secondary);Button("获取电脑历史"){load(next:false)}.disabled(loading || agent.isEmpty);ForEach(entries,id:\.self){entry in Button(entry["title"].string){guard let pc else{return};Task{do{let r=try await workspace.mutate(pc:pc,action:"import-native",extra:["agentId":.string(agent),"requestId":entry["nativeId"]]);if let s=workspace.conversations[r["session"]["id"].string]{workspace.open(s);dismiss()}}catch{product.report(error)}}}};if !cursor.isEmpty{Button("加载更多"){load(next:true)}.disabled(loading)}}.navigationTitle("电脑 AI 历史").toolbar{Button("关闭"){dismiss()}}.onAppear{pc=product.pcs.first{$0.online}?.id;agent=agents.first?["id"].string ?? ""}.onChange(of:pc){_,_ in agent=agents.first?["id"].string ?? "";entries=[];cursor=""}.onChange(of:agent){_,_ in entries=[];cursor=""}}
    private func load(next:Bool){guard let pc else{return};let requested=agent;loading=true;Task{defer{loading=false};do{let page=try await workspace.nativeHistory(pc:pc,agent:requested,cursor:next ? cursor:"");guard self.pc==pc,agent==requested,page["sessions"].array.count<=500 else{return};entries=next ? entries+page["sessions"].array:page["sessions"].array;cursor=page["nextCursor"].string}catch{product.report(error)}}}
}
