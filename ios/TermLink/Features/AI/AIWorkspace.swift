// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import CryptoKit
import Combine
import UniformTypeIdentifiers

@MainActor final class AIWorkspace: ObservableObject {
    struct Opened: Codable,Identifiable { let pcID:UUID,id:String,agentID:String }
    struct Mutation: Codable { let id:UUID,pcID:UUID,body:JSONValue }
    struct Queued: Codable,Identifiable {
        let id:String,text:String,host:JSONValue,controlVersion:JSONValue
        var failed=false,restored=false
        func canSend(_ session:AIConversation,host:JSONValue)->Bool{!failed && !restored && self.host==host && controlVersion==session.raw["controlVersion"] && session.canControl && session.state=="idle" && session.pending.isEmpty}
    }
    struct Attachment: Codable,Identifiable {
        let id:String,host:String,controlVersion:JSONValue,name:String,size:Int64,mime:String
        var sent:Int64,status:String
    }
    @Published private(set) var catalogs:[UUID:JSONValue]=[:],conversations:[String:AIConversation]=[:]
    @Published private(set) var opened:[Opened]=[],uncertain:[UUID:Mutation]=[:]
    @Published var selected:String?,drafts:[String:String]=[:],references:[String:[String]]=[:],search="",attentionOnly=false
    @Published private(set) var busy=Set<UUID>(),unavailable=Set<UUID>()
    @Published private(set) var attachments:[String:[Attachment]]=[:]
    @Published private(set) var queued:[String:[Queued]]=[:]
    @Published private(set) var pinned=Set<String>()
    @Published var externalLink:URL?,directoryLocation:(UUID,String)?,exportURL:URL?
    private unowned let product:ProductStore
    private var polling=false,epoch=UUID(),reading=Set<String>(),preparingUploads=Set<String>()
    init(product:ProductStore){self.product=product}
    func restore(){do{opened=try product.privateState.load("ai-opened",as:[Opened].self) ?? [];drafts=try product.privateState.load("ai-drafts",as:[String:String].self) ?? [:];references=try product.privateState.load("ai-references",as:[String:[String]].self) ?? [:];uncertain=try product.privateState.load("ai-pending",as:[UUID:Mutation].self) ?? [:];pinned=try product.privateState.load("ai-pinned",as:Set<String>.self) ?? [];queued=try product.privateState.load("ai-queue",as:[String:[Queued]].self) ?? [:];for (id,items) in queued{queued[id]=items.map{var item=$0;item.restored=true;return item}}}catch{product.report(error)}}
    func persist(){do{try product.privateState.save(opened,name:"ai-opened");try product.privateState.save(drafts,name:"ai-drafts");try product.privateState.save(references,name:"ai-references");try product.privateState.save(uncertain,name:"ai-pending");try product.privateState.save(pinned,name:"ai-pinned");try product.privateState.save(queued,name:"ai-queue")}catch{product.report(error)}}
    func reset(){epoch=UUID();catalogs=[:];conversations=[:];opened=[];uncertain=[:];selected=nil;drafts=[:];references=[:];attachments=[:];queued=[:];pinned=[];busy=[];unavailable=[];reading=[];preparingUploads=[];polling=false;externalLink=nil;directoryLocation=nil;exportURL=nil}
    func markUnavailable(_ pc:UUID){unavailable.insert(pc);conversations=conversations.filter{$0.value.pcID != pc}}
    func invalidate(){epoch=UUID();unavailable=Set(product.pcs.map(\.id));conversations=[:];reading=[];busy=[];polling=false}
    func refreshAll() async {
        guard !polling,product.relay?.state == .connected else{return};let owner=epoch;polling=true;defer{if owner==epoch{polling=false}}
        for pc in product.pcs where pc.online && pc.routing && pc.capabilities.contains("ai_agent_v1"){
            do{try await refresh(pc.id);if let selected,conversations[selected]?.pcID==pc.id{try await refresh(pc.id,session:selected)}}catch is CancellationError{return}catch{if owner==epoch{unavailable.insert(pc.id)}}
        }
    }
    func refresh(_ pc:UUID,session:String?=nil) async throws {
        guard let relay=product.relay else{throw ClientError.readOnly};let owner=epoch
        let slot=pc.uuidString+":"+(session ?? "catalog");guard !reading.contains(slot) else{return};reading.insert(slot);defer{if owner==epoch{reading.remove(slot)}}
        var body:[String:JSONValue]=["action":.string("catalog")]
        if let session,let host=catalogs[pc]?["hostId"].string,!host.isEmpty{body=["action":.string("poll"),"hostId":.string(host),"sessionId":.string(session)]}
        let value=try await relay.ai(pc:pc,body:.object(body));try check(value);guard owner==epoch else{throw CancellationError()}
        if body["action"] == .string("catalog") {
            guard UUID(uuidString:value["hostId"].string) != nil,value["agents"].array.count<=16,value["sessions"].array.count<=64 else{throw ClientError.invalidResponse("AI catalog")}
            if let old=catalogs[pc],old["hostId"] != value["hostId"]{
                let ids=Set(opened.filter{$0.pcID==pc}.map(\.id))
                conversations=conversations.filter{$0.value.pcID != pc};attachments=attachments.filter{!ids.contains($0.key)}
            }
            let visible=Set(value["sessions"].array.map{$0["id"].string})
            conversations=conversations.filter{$0.value.pcID != pc || visible.contains($0.key)}
            catalogs[pc]=value;for s in value["sessions"].array{merge(s,pc:pc,metadata:true)}
        } else {guard value["hostId"]==catalogs[pc]?["hostId"] else{throw ClientError.readOnly}}
        if value["session"] != .null{merge(value["session"],pc:pc)}
        unavailable.remove(pc)
        if let session,let current=conversations[session]{await drain(current)}
    }
    private func merge(_ raw:JSONValue,pc:UUID,metadata:Bool=false){guard UUID(uuidString:raw["id"].string) != nil,raw["kind"].string=="ai",raw["messages"].array.count<=8192,raw["pending"].array.count<=64 else{return};var object=raw.object
        if metadata,let old=conversations[raw["id"].string]{object["messages"] = .array(old.messages);object["pending"] = .array(old.pending);object["historyBefore"]=old.raw["historyBefore"]}
        if let old=conversations[raw["id"].string],!raw["reset"].bool,old.raw["streamId"]==raw["streamId"],raw["capabilities"]["history"].bool{
            var ids=Set<String>();let latest=Dictionary(raw["messages"].array.map{($0["id"].string,$0)},uniquingKeysWith:{_,new in new});object["messages"] = .array(Array((old.messages.map{latest[$0["id"].string] ?? $0}+raw["messages"].array).filter{ids.insert($0["id"].string).inserted}.suffix(8192)))
            object["historyBefore"] = .number(Double(min(old.raw["historyBefore"].integer,raw["historyBefore"].integer)))
        }
        conversations[raw["id"].string] = .init(raw:.object(object),pcID:pc)
        if let session=conversations[raw["id"].string]{product.notifications.observe(session)}
    }
    func open(_ s:AIConversation){if !opened.contains(where:{$0.id==s.id}){opened.append(.init(pcID:s.pcID,id:s.id,agentID:s.raw["agentId"].string))};selected=s.id;product.tab = .ai;persist()}
    func closeView(_ id:String){opened.removeAll{$0.id==id};if selected==id{selected=nil};persist()}
    func togglePin(_ id:String){if !pinned.insert(id).inserted{pinned.remove(id)};persist()}
    func enqueue(_ s:AIConversation) throws {
        let text=drafts[s.id] ?? "";guard s.canControl,!text.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty,text.count<=16000,(attachments[s.id] ?? []).isEmpty,(references[s.id] ?? []).isEmpty,(queued[s.id] ?? []).count<8,let host=catalogs[s.pcID]?["hostId"] else{throw ClientError.readOnly}
        var next=queued;next[s.id,default:[]].append(.init(id:UUID().uuidString.lowercased(),text:text,host:host,controlVersion:s.raw["controlVersion"]))
        try product.privateState.save(next,name:"ai-queue");queued=next;drafts[s.id]="";persist()
    }
    func recoverQueued(_ session:String,id:String) throws {
        guard uncertain.values.allSatisfy({$0.body["queueId"].string != id}),let item=queued[session]?.first(where:{$0.id==id}) else{throw ClientError.readOnly}
        try appendDraft(session,item.text);queued[session]?.removeAll{$0.id==id};persist()
    }
    private func drain(_ s:AIConversation) async {
        guard let item=queued[s.id]?.first,item.canSend(s,host:catalogs[s.pcID]?["hostId"] ?? .null),!busy.contains(s.pcID),uncertain[s.pcID]==nil,!unavailable.contains(s.pcID) else{return}
        let owner=epoch
        do{_=try await mutate(pc:s.pcID,action:"send",session:s,extra:["text":.string(item.text),"queueId":.string(item.id)]);guard owner==epoch else{return};queued[s.id]?.removeAll{$0.id==item.id};persist()}
        catch{if owner==epoch,let index=queued[s.id]?.firstIndex(where:{$0.id==item.id}){queued[s.id]?[index].failed=true;persist()}}
    }
    func mutate(pc:UUID,action:String,session:AIConversation?=nil,extra:[String:JSONValue]=[:]) async throws -> JSONValue {
        guard let relay=product.relay,let host=catalogs[pc]?["hostId"].string,!host.isEmpty,!busy.contains(pc),uncertain[pc]==nil,!unavailable.contains(pc) else{throw ClientError.readOnly}
        if let session,!["control","resume","forget"].contains(action),!session.canControl{throw ClientError.readOnly}
        let id=UUID();var body=extra;body["action"] = .string(action);body["hostId"] = .string(host);body["operationId"] = .string(id.uuidString.lowercased())
        if let session{body["sessionId"] = .string(session.id);body["controlVersion"] = session.raw["controlVersion"]}
        let mutation=Mutation(id:id,pcID:pc,body:.object(body)),owner=epoch
        var retained=uncertain;retained[pc]=mutation;try product.privateState.save(retained,name:"ai-pending")
        uncertain=retained;busy.insert(pc);defer{if owner==epoch{busy.remove(pc)}}
        do{let result=try await relay.ai(pc:pc,body:mutation.body);guard owner==epoch,result["hostId"].string==host else{throw ClientError.readOnly};uncertain.removeValue(forKey:pc);persist();try check(result);if result["session"] != .null{merge(result["session"],pc:pc)}
            if action=="fork",let created=conversations[result["session"]["id"].string]{open(created)}
            if action=="end",let session{closeView(session.id)}
            if action=="steer",let session,drafts[session.id]==extra["text"]?.string{drafts[session.id]="";persist()}
            return result}
        catch{if owner==epoch{persist()};throw error}
    }
    func retryUncertain(_ pc:UUID) async throws {
        guard let mutation=uncertain[pc],let relay=product.relay,!busy.contains(pc) else{return}
        let owner=epoch;busy.insert(pc);defer{if owner==epoch{busy.remove(pc)}}
        // Retry exactly the original host-bound operation ID and body, never a new mutation.
        let result=try await relay.ai(pc:pc,body:mutation.body)
        guard owner==epoch,result["hostId"]==mutation.body["hostId"] else{throw ClientError.readOnly}
        uncertain.removeValue(forKey:pc);persist();try check(result);if result["session"] != .null{merge(result["session"],pc:pc)}
        let session=mutation.body["sessionId"].string,queue=mutation.body["queueId"].string
        if !queue.isEmpty{queued[session]?.removeAll{$0.id==queue}}
        else if mutation.body["action"].string=="send"{if drafts[session]==mutation.body["text"].string{drafts[session]=""};attachments[session]=[];references[session]=[]}
        if mutation.body["action"].string=="end"{closeView(session)}
        if mutation.body["action"].string=="forget"{
            guard result["deletedSessionId"].string==session else{throw ClientError.protocolViolation("deleted AI session")}
            conversations.removeValue(forKey:session);attachments.removeValue(forKey:session);queued.removeValue(forKey:session);drafts.removeValue(forKey:session);references.removeValue(forKey:session);pinned.remove(session);closeView(session)
        }
        persist()
        if ["create","fork","import-native"].contains(mutation.body["action"].string),let created=conversations[result["session"]["id"].string]{open(created)}
    }
    func create(pc:UUID,agent:String,directory:String,options:[String:JSONValue]=[:]) async throws {
        guard catalogs[pc]?["agents"].array.contains(where:{$0["id"].string==agent && AgentBrand.available($0,nativeOnly:true)})==true else{throw ClientError.readOnly}
        let allowed=Set(["directoryMode","permissionMode","modelId","providerId","reasoningEffort","confirmedPermissionMode"])
        guard options.keys.allSatisfy({allowed.contains($0)}) else{throw ClientError.readOnly}
        var extra=options;extra["agentId"] = .string(agent);extra["directory"] = .string(directory)
        let value=try await mutate(pc:pc,action:"create",extra:extra)
        guard let s=conversations[value["session"]["id"].string] else{throw ClientError.invalidResponse("AI session")};open(s)
    }
    func send(_ s:AIConversation) async throws {
        let text=drafts[s.id] ?? "",files=attachments[s.id] ?? []
        guard !text.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty || !files.isEmpty else{return}
        guard files.allSatisfy({$0.status=="ready" && $0.controlVersion==s.raw["controlVersion"] && $0.host==catalogs[s.pcID]?["hostId"].string})else{throw ClientError.readOnly}
        _=try await mutate(pc:s.pcID,action:"send",session:s,extra:["text":.string(text),"attachmentIds":.array(files.map{.string($0.id)}),"fileReferences":.array((references[s.id] ?? []).map(JSONValue.string))])
        attachments[s.id]=[];references[s.id]=[];if drafts[s.id]==text{drafts[s.id]="";persist()}
    }
    func read(_ s:AIConversation,action:String,extra:[String:JSONValue]=[:]) async throws -> JSONValue {
        guard let relay=product.relay,let host=catalogs[s.pcID]?["hostId"].string,!unavailable.contains(s.pcID) else{throw ClientError.readOnly}
        var body=extra;body["action"] = .string(action);body["hostId"] = .string(host);body["sessionId"] = .string(s.id)
        let owner=epoch,result=try await relay.ai(pc:s.pcID,body:.object(body));guard owner==epoch,result["hostId"].string==host else{throw ClientError.readOnly};try check(result);return result
    }
    func history(_ s:AIConversation) async throws {
        guard s.raw["historyBefore"].integer>0 else{return}
        let value=try await read(s,action:"history",extra:["offset":s.raw["historyBefore"],"streamId":s.raw["streamId"]]),page=value["history"]
        guard case .array = page["messages"],case .number = page["before"] else{throw ClientError.invalidResponse("AI history page")}
        guard let current=conversations[s.id],current.raw["streamId"]==s.raw["streamId"],page["messages"].array.count<=64,page["before"].integer>=0,page["before"].integer<s.raw["historyBefore"].integer else{throw ClientError.invalidResponse("AI history cursor")}
        var ids=Set<String>(),object=current.raw.object
        object["messages"] = .array((page["messages"].array+current.messages).filter{ids.insert($0["id"].string).inserted});object["historyBefore"]=page["before"]
        conversations[s.id] = .init(raw:.object(object),pcID:s.pcID)
    }
    func nativeHistory(pc:UUID,agent:String,cursor:String="") async throws -> JSONValue {
        guard let relay=product.relay,let host=catalogs[pc]?["hostId"].string else{throw ClientError.readOnly}
        let owner=epoch,result=try await relay.ai(pc:pc,body:.object(["action":.string("native-history"),"hostId":.string(host),"agentId":.string(agent),"text":.string(cursor)]));guard owner==epoch,result["hostId"].string==host,result["agentId"].string==agent else{throw ClientError.readOnly};try check(result)
        guard case .array = result["nativeHistory"]["sessions"],result["nativeHistory"]["sessions"].array.count<=500 else{throw ClientError.invalidResponse("native history page")};return result["nativeHistory"]
    }
    func appendDraft(_ id:String,_ text:String) throws{let next=[drafts[id] ?? "",text].filter{!$0.isEmpty}.joined(separator:"\n\n");guard next.count<=16000 else{throw ClientError.limitExceeded};drafts[id]=next;persist()}
    func webState(_ s:AIConversation)->JSONValue{do{return try product.privateState.load("ai-web:"+s.pcID.uuidString+":"+s.id,as:JSONValue.self) ?? .object([:])}catch{return .object([:])}}
    func saveWebState(_ s:AIConversation,_ state:JSONValue) throws{guard try state.data().count<=65536 else{throw ClientError.limitExceeded};var allowed=state.object.filter{["tail","anchor","expanded","questions"].contains($0.key)}
        let ids=Set(s.pending.map{$0["id"].string});allowed["questions"] = .object(state["questions"].object.filter{ids.contains($0.key)})
        try product.privateState.save(JSONValue.object(allowed),name:"ai-web:"+s.pcID.uuidString+":"+s.id)}
    func openDirectory(_ s:AIConversation,path:String){let parent=(path as NSString).deletingLastPathComponent;directoryLocation=(s.pcID,s.directory+"/"+parent)}
    func configure(_ s:AIConversation,model:String?=nil,provider:String?=nil,mode:String?=nil,effort:String?=nil,confirmed:String?=nil) async throws{
        guard let current=conversations[s.id],current.pcID==s.pcID,current.raw["controlVersion"]==s.raw["controlVersion"],current.raw["settingsVersion"]==s.raw["settingsVersion"],current.state=="idle",current.pending.isEmpty else{throw ClientError.readOnly}
        let config=current.capabilities["configuration"]
        if model != nil || provider != nil{guard config["modelSwitch"].bool else{throw ClientError.readOnly}}
        if let effort{guard config["reasoningSwitch"].bool,config["reasoningScope"].string != "new_session",current.raw["reasoning"]["options"].array.contains(where:{$0["value"].string==effort}) else{throw ClientError.readOnly}}
        if let mode{guard config["permissionSwitch"].bool,let option=config["permissionModes"].array.first(where:{$0["id"].string==mode}),option["requiresConfirmation"] == .bool(false) || mode==current.raw["permissionMode"].string || confirmed==mode else{throw ClientError.readOnly}}
        var extra:[String:JSONValue]=["modelId":.string(model ?? s.raw["modelId"].string),"providerId":.string(provider ?? s.raw["providerId"].string),"permissionMode":.string(mode ?? s.raw["permissionMode"].string),"expectedSettingsVersion":s.raw["settingsVersion"]]
        if let effort{extra["reasoningEffort"] = .string(effort)};if let confirmed{extra["confirmedPermissionMode"] = .string(confirmed)}
        _=try await mutate(pc:s.pcID,action:"configure",session:s,extra:extra)
    }
    private func validateModels(_ value:JSONValue,agent:String) throws{
        guard value["agentId"].string==agent,value["models"].array.count<=200 else{throw ClientError.invalidResponse("AI models")}
        for model in value["models"].array{let id=model["modelId"].string,provider=model["providerId"].string;guard !id.isEmpty,id.count<=200,provider.count<=200,!((id+provider).unicodeScalars.contains{CharacterSet.controlCharacters.contains($0)}),agent != "zcode" || !provider.isEmpty else{throw ClientError.invalidResponse("AI model identity")}}
    }
    func models(_ s:AIConversation) async throws->[JSONValue]{let value=try await read(s,action:"models",extra:["agentId":s.raw["agentId"]]);try validateModels(value,agent:s.raw["agentId"].string);return value["models"].array}
    func modelCatalog(pc:UUID,agent:String) async throws->JSONValue {
        guard let relay=product.relay,let host=catalogs[pc]?["hostId"],!unavailable.contains(pc) else{throw ClientError.readOnly}
        let owner=epoch,result=try await relay.ai(pc:pc,body:.object(["action":.string("models"),"hostId":host,"agentId":.string(agent)]))
        guard owner==epoch,result["hostId"]==host,catalogs[pc]?["hostId"]==host else{throw ClientError.readOnly};try check(result);try validateModels(result,agent:agent);return result
    }
    func directories(pc:UUID,path:String,offset:Int64,includeFiles:Bool) async throws->JSONValue{
        guard let relay=product.relay,let catalog=catalogs[pc],catalog["directoryBrowsing"].bool,!unavailable.contains(pc),path.utf8.count<=4096,!path.contains("\0"),offset>=0 else{throw ClientError.readOnly}
        let owner=epoch,host=catalog["hostId"],result=try await relay.ai(pc:pc,body:.object(["action":.string(includeFiles ? "files":"directories"),"hostId":host,"directory":.string(path),"offset":.number(Double(offset))]))
        guard owner==epoch,result["hostId"]==host,catalogs[pc]?["hostId"]==host else{throw ClientError.readOnly};try check(result)
        let page=result["directories"];guard page["entries"].array.count<=500,page["currentPath"].string.utf8.count<=4096,page["nextOffset"] == .null || page["nextOffset"].integer>offset else{throw ClientError.invalidResponse("AI directory page")}
        for entry in page["entries"].array{guard entry["kind"].string=="directory" || includeFiles && entry["kind"].string=="file",!entry["path"].string.isEmpty,entry["path"].string.utf8.count<=4096,!entry["path"].string.contains("\0") else{throw ClientError.invalidResponse("AI directory entry")}}
        return page
    }
    func addReference(_ s:AIConversation,path:String) throws{
        guard s.canControl,s.capabilities["attachments"].bool,!path.isEmpty,path.utf8.count<=4096,!path.contains("\0"),(references[s.id] ?? []).count<5 else{throw ClientError.readOnly}
        if references[s.id]?.contains(path) != true{references[s.id,default:[]].append(path);persist()}
    }
    func forget(_ s:AIConversation) async throws{
        guard ["exited","interrupted","suspended"].contains(s.state),s.capabilities["history"].bool else{throw ClientError.readOnly}
        let result=try await mutate(pc:s.pcID,action:"forget",session:s)
        guard result["deletedSessionId"].string==s.id else{throw ClientError.protocolViolation("deleted AI session")}
        conversations.removeValue(forKey:s.id);attachments.removeValue(forKey:s.id);queued.removeValue(forKey:s.id);drafts.removeValue(forKey:s.id);references.removeValue(forKey:s.id);pinned.remove(s.id);closeView(s.id)
    }
    func remoteAddress(_ pc:UUID) async throws->URL {
        guard let relay=product.relay,let catalog=catalogs[pc],!unavailable.contains(pc),catalog["agents"].array.contains(where:{$0["id"].string=="zcode" && $0["strategy"].string=="desktop_remote" && AgentBrand.available($0)}) else{throw ClientError.readOnly}
        let owner=epoch,host=catalog["hostId"],result=try await relay.ai(pc:pc,body:.object(["action":.string("remote"),"hostId":host,"agentId":.string("zcode")]))
        guard owner==epoch,result["hostId"]==host,catalogs[pc]?["hostId"]==host,result["strategy"].string=="desktop_remote",result["agentId"].string=="zcode",let url=URL(string:result["remoteUrl"].string),ZCodeRemoteURL.allowed(url) else{throw ClientError.readOnly};try check(result);return url
    }
    func exportMarkdown(_ s:AIConversation) async throws {
        guard s.capabilities["history"].bool else{throw ClientError.readOnly}
        let owner=epoch;var before:Int64=0,pages:[[JSONValue]]=[]
        repeat{let result=try await read(s,action:"history",extra:["offset":.number(Double(before))]),page=result["history"],next=page["before"].integer
            guard case .array = page["messages"],case .number = page["before"] else{throw ClientError.invalidResponse("export page")}
            guard pages.count<10000,page["messages"].array.count<=64,next>=0,before==0 || next<before else{throw ClientError.invalidResponse("export history cursor")};pages.append(page["messages"].array);before=next
        }while before>0
        var text="# "+s.title+"\n\n"
        for message in pages.reversed().flatMap({$0}){
            let body=message["fullLength"].integer>Int64(message["text"].string.utf16.count) ? try await fullMessage(s,id:message["id"].string):message["text"].string
            text+="## "+message["role"].string+"\n\n"+body+"\n\n";guard text.utf8.count<=16*1024*1024 else{throw ClientError.limitExceeded}
        }
        guard owner==epoch else{throw CancellationError()}
        guard text.utf8.count<=16*1024*1024 else{throw ClientError.limitExceeded}
        let root=FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString,isDirectory:true);try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
        let target=root.appendingPathComponent("termlink-"+String(s.id.prefix(8))+".md");try Data(text.utf8).write(to:target,options:[.atomic,.completeFileProtection]);exportURL=target
    }
    func fullMessage(_ s:AIConversation,id:String) async throws->String {
        guard !id.isEmpty,id.count<=256 else{throw ClientError.limitExceeded}
        var offset:Int64=0,output="",expected:Int64?
        repeat{let result=try await read(s,action:"message",extra:["requestId":.string(id),"offset":.number(Double(offset))]),page=result["message"],length=page["length"].integer,next=page["nextOffset"].integer
            guard page["id"].string==id,page["offset"].integer==offset,(0...1048577).contains(length),next>=offset,next<=length,next>offset || next==length,expected==nil || expected==length else{throw ClientError.protocolViolation("message range")}
            let chunk=page["text"].string;guard Int64(chunk.utf16.count)==next-offset else{throw ClientError.protocolViolation("message length")};output+=chunk;guard output.utf16.count<=1048577 else{throw ClientError.limitExceeded};offset=next;expected=length
        }while offset<(expected ?? 0)
        return output
    }
    func upload(_ s:AIConversation,url:URL) async throws{
        guard s.canControl,s.capabilities["attachments"].bool,!preparingUploads.contains(s.id),let relay=product.relay,let host=catalogs[s.pcID]?["hostId"].string,let maxFiles=Int(exactly:s.capabilities["maxFiles"].integer),(attachments[s.id] ?? []).count<min(5,maxFiles),(1...24576).contains(s.capabilities["chunkBytes"].integer) else{throw ClientError.readOnly}
        let access=url.startAccessingSecurityScopedResource();defer{if access{url.stopAccessingSecurityScopedResource()}}
        let maxBytes=min(20*1024*1024,s.capabilities["maxFileBytes"].integer),values=try url.resourceValues(forKeys:[.fileSizeKey,.contentTypeKey,.isRegularFileKey])
        guard values.isRegularFile==true,let size=values.fileSize,size>0,Int64(size)<=maxBytes,
              Int64(size)+(attachments[s.id] ?? []).reduce(Int64(0),{$0+$1.size})<=min(50*1024*1024,s.capabilities["maxTotalBytes"].integer) else{throw ClientError.limitExceeded}
        let id=UUID().uuidString.lowercased(),owner=epoch
        preparingUploads.insert(s.id);defer{if owner==epoch{preparingUploads.remove(s.id)}}
        let digest=try await FileDigest.sha256(url,size:UInt64(size),limit:UInt64(maxBytes)).hex
        guard owner==epoch,conversations[s.id]?.raw["controlVersion"]==s.raw["controlVersion"],catalogs[s.pcID]?["hostId"].string==host else{throw CancellationError()}
        let handle=try FileHandle(forReadingFrom:url);defer{try? handle.close()}
        let name=String(url.lastPathComponent.prefix(255)),mime=values.contentType?.preferredMIMEType ?? "application/octet-stream"
        var entry=Attachment(id:id,host:host,controlVersion:s.raw["controlVersion"],name:name,size:Int64(size),mime:mime,sent:0,status:"uploading")
        attachments[s.id,default:[]].append(entry)
        func request(_ action:String,_ extra:[String:JSONValue]=[:]) async throws->JSONValue{
            guard self.epoch==owner,self.catalogs[s.pcID]?["hostId"].string==host,self.conversations[s.id]?.raw["controlVersion"]==s.raw["controlVersion"] else{throw ClientError.readOnly}
            var body=extra;body["action"] = .string(action);body["hostId"] = .string(host);body["sessionId"] = .string(s.id);body["controlVersion"]=s.raw["controlVersion"];body["attachmentId"] = .string(id)
            let r=try await relay.ai(pc:s.pcID,body:.object(body));guard self.epoch==owner,r["hostId"].string==host else{throw ClientError.readOnly};try self.check(r);return r
        }
        do{let begin=try await request("attachment.begin",["fileName":.string(name),"mime":.string(mime),"size":.number(Double(size)),"sha256":.string(digest)])
            entry.sent=begin["attachment"]["offset"].integer;guard entry.sent>=0,entry.sent<=Int64(size) else{throw ClientError.protocolViolation("attachment offset")};try handle.seek(toOffset:UInt64(entry.sent))
            if !begin["attachment"]["ready"].bool{
                while entry.sent<Int64(size){try Task.checkCancellation();let maxChunk=max(1,min(24576,Int(s.capabilities["chunkBytes"].integer)));guard let chunk=try handle.read(upToCount:maxChunk),!chunk.isEmpty else{throw ClientError.invalidResponse("attachment bytes")};let r=try await request("attachment.chunk",["offset":.number(Double(entry.sent)),"data":.string(chunk.base64EncodedString())]);entry.sent+=Int64(chunk.count);guard r["attachment"]["offset"].integer==entry.sent else{throw ClientError.protocolViolation("attachment acknowledgement")};if let i=attachments[s.id]?.firstIndex(where:{$0.id==id}){attachments[s.id]?[i]=entry}}
                let committed=try await request("attachment.commit");guard committed["attachment"]["ready"].bool else{throw ClientError.invalidResponse("attachment commit")}
            }
            entry.status="ready";if let i=attachments[s.id]?.firstIndex(where:{$0.id==id}){attachments[s.id]?[i]=entry}
        }catch{entry.status="failed";if let i=attachments[s.id]?.firstIndex(where:{$0.id==id}){attachments[s.id]?[i]=entry};throw error}
    }
    func removeAttachment(_ s:AIConversation,id:String) async throws{guard let item=attachments[s.id]?.first(where:{$0.id==id}) else{return};guard item.status != "uploading" else{throw ClientError.unavailable("请等待上传结束。")}
        if item.status != "failed"{_=try await read(s,action:"attachment.remove",extra:["attachmentId":.string(id),"controlVersion":s.raw["controlVersion"]])};attachments[s.id]?.removeAll{$0.id==id}}
    func downloadArtifact(_ s:AIConversation,file:String,version:String) async throws{
        guard !version.isEmpty,version.count<=100 else{throw ClientError.invalidResponse("artifact version")}
        var offset:Int64=0,data=Data(),expected:Int64?;let returnedVersion=version
        repeat{let r=try await read(s,action:"artifact.read",extra:["requestId":.string(file),"offset":.number(Double(offset)),"cursor":.string(returnedVersion)]);let a=r["artifacts"];let chunk=try Data(base64URL:a["data"].string)
            guard a["id"].string==file,a["version"].string==returnedVersion,a["offset"].integer==offset,a["size"].integer>=0,a["size"].integer<=50*1024*1024,chunk.count<=65536,a["nextOffset"].integer==offset+Int64(chunk.count),a["nextOffset"].integer<=a["size"].integer,!chunk.isEmpty || offset==a["size"].integer else{throw ClientError.protocolViolation("artifact range")}
            if let expected{guard expected==a["size"].integer else{throw ClientError.readOnly}}else{expected=a["size"].integer};data.append(chunk);offset+=Int64(chunk.count)
        }while offset<(expected ?? 0)
        let root=FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString,isDirectory:true);try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
        let name=(file as NSString).lastPathComponent;guard !name.isEmpty,name != ".",name != ".." else{throw ClientError.invalidResponse("artifact name")};let target=root.appendingPathComponent(name);try data.write(to:target,options:[.atomic,.completeFileProtection]);exportURL=target
    }
    private func check(_ result:JSONValue) throws {if !result["error"].string.isEmpty{throw ClientError.unavailable(result["error"].string)};if result["ok"] == .bool(false){throw ClientError.unavailable(result["errorCode"].string)}}
}
