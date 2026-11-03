// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import CryptoKit
import Combine
import UniformTypeIdentifiers

/// Content validation is independent of host/control arbitration and never rewrites paths or text.
enum AIMessageContent {
    struct ComposerRemainder: Equatable {
        let draft: String?
        let attachmentIDs: [String]
        let fileReferences: [String]
    }

    static func hasContent(text: String, attachmentCount: Int, fileReferences: [String]) throws -> Bool {
        guard fileReferences.count <= 5 else { throw ClientError.limitExceeded }
        for path in fileReferences {
            guard path.utf8.count <= 4096 else { throw ClientError.limitExceeded }
            guard !path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !path.contains("\0") else {
                throw ClientError.invalidResponse("AI file reference")
            }
        }
        return !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || attachmentCount > 0 || !fileReferences.isEmpty
    }

    /// A receipt acknowledges its exact payload, never content added while it was in flight.
    static func remainingAfterAcknowledgement(request: JSONValue, draft: String?, attachmentIDs: [String],
                                               fileReferences: [String]) -> ComposerRemainder {
        let unchanged = ComposerRemainder(draft: draft, attachmentIDs: attachmentIDs, fileReferences: fileReferences)
        guard request["action"] == .string("send"), request.object["queueId"] == nil,
              case .string(let sentText) = request["text"],
              case .array(let sentAttachments) = request["attachmentIds"],
              case .array(let sentReferences) = request["fileReferences"] else { return unchanged }
        let isNonemptyString: (JSONValue) -> Bool = { value in
            if case .string(let text) = value { return !text.isEmpty }; return false
        }
        guard sentAttachments.allSatisfy(isNonemptyString), sentReferences.allSatisfy(isNonemptyString) else { return unchanged }
        let sentIDs = Set(sentAttachments.map(\.string)), sentPaths = Set(sentReferences.map(\.string))
        return ComposerRemainder(draft: draft == sentText ? "" : draft,
            attachmentIDs: attachmentIDs.filter { !sentIDs.contains($0) },
            fileReferences: fileReferences.filter { !sentPaths.contains($0) })
    }
}

@MainActor final class AIWorkspace: ObservableObject {
    struct Opened: Codable,Identifiable,Equatable {
        let pcID:UUID,id:String,agentID:String,strategy:String
        var isNative:Bool{strategy=="native" && !id.hasPrefix("remote:")}
        init(pcID:UUID,id:String,agentID:String,strategy:String="native") {
            self.pcID=pcID;self.id=id;self.agentID=agentID;self.strategy=strategy
        }
        private enum CodingKeys:String,CodingKey{case pcID,id,agentID,strategy}
        init(from decoder:Decoder) throws {
            let values=try decoder.container(keyedBy:CodingKeys.self)
            pcID=try values.decode(UUID.self,forKey:.pcID);id=try values.decode(String.self,forKey:.id)
            agentID=try values.decode(String.self,forKey:.agentID)
            strategy=values.contains(.strategy) ? try values.decodeIfPresent(String.self,forKey:.strategy) ?? "":"native"
        }
        static func retainingNative(_ opened:[Opened],selected:String?) -> (opened:[Opened],selected:String?) {
            let removed=Set(opened.filter{!$0.isNative}.map(\.id))
            return (opened.filter(\.isNative),selected.flatMap{removed.contains($0) || $0.hasPrefix("remote:") ? nil:$0})
        }
    }
    struct Mutation: Codable { let id:UUID,pcID:UUID,body:JSONValue }
    struct Queued: Codable,Identifiable {
        let id:String,text:String,host:JSONValue,controlVersion:JSONValue
        var failed=false,restored=false
        func canSend(_ session:AIConversation,host:JSONValue)->Bool{!failed && !restored && self.host==host && controlVersion==session.raw["controlVersion"] && session.canControl && session.state=="idle" && session.pending.isEmpty}
    }
    typealias Attachment = AIStagedAttachment
    @Published private(set) var catalogs:[UUID:JSONValue]=[:]
    @Published private(set) var conversations:[String:AIConversation]=[:]
    @Published private(set) var opened:[Opened]=[]
    @Published private(set) var uncertain:[UUID:Mutation]=[:]
    @Published var selected:String?
    @Published var drafts:[String:String]=[:]
    @Published var references:[String:[String]]=[:]
    @Published var search=""
    @Published var attentionOnly=false
    @Published private(set) var busy=Set<UUID>()
    @Published private(set) var unavailable=Set<UUID>()
    @Published private(set) var attachments:[String:[Attachment]]=[:]
    @Published private(set) var queued:[String:[Queued]]=[:]
    @Published private(set) var pinned=Set<String>()
    @Published var externalLink:URL?
    @Published var directoryLocation:(UUID,String)?
    @Published var exportURL:URL?
    private unowned let product:ProductStore
    private var polling=false,epoch=UUID(),reading=Set<String>(),preparingUploads=Set<String>()
    private var attachmentCache: AIAttachmentCache?
    init(product:ProductStore){self.product=product}
    func restore() async {
        let owner = epoch
        do {
            let storage = product.privateState
            opened = try storage.load("ai-opened", as: [Opened].self) ?? []
            let previousViews = opened
            discardLegacyViews()
            if opened != previousViews {
                do { try storage.save(opened, name: "ai-opened") } catch { product.report(error) }
            }
            drafts = try storage.load("ai-drafts", as: [String: String].self) ?? [:]
            references = try storage.load("ai-references", as: [String: [String]].self) ?? [:]
            uncertain = try storage.load("ai-pending", as: [UUID: Mutation].self) ?? [:]
            pinned = try storage.load("ai-pinned", as: Set<String>.self) ?? []
            queued = try storage.load("ai-queue", as: [String: [Queued]].self) ?? [:]
            for (id, items) in queued { queued[id] = items.map { var item = $0; item.restored = true; return item } }
            attachments = Attachment.recover(try storage.load("ai-attachments", as: [String: [Attachment]].self) ?? [:])
            let cache = try uploadCache()
            try await cache.prune(retaining: Set(attachments.values.flatMap { $0.map(\.id) }))
            guard owner == epoch else { throw CancellationError() }
            try saveAttachments()
        } catch { product.report(error) }
    }
    func persist() {
        do {
            discardLegacyViews()
            try saveAttachments()
            let storage = product.privateState
            try storage.save(opened, name: "ai-opened"); try storage.save(drafts, name: "ai-drafts")
            try storage.save(references, name: "ai-references"); try storage.save(uncertain, name: "ai-pending")
            try storage.save(pinned, name: "ai-pinned"); try storage.save(queued, name: "ai-queue")
        } catch { product.report(error) }
    }
    private func discardLegacyViews(){let retained=Opened.retainingNative(opened,selected:selected);if opened != retained.opened{opened=retained.opened};if selected != retained.selected{selected=retained.selected}}
    func requiresHostUpgrade(_ pc:UUID,agent:String="zcode")->Bool{AgentBrand.requiresHostUpgrade(catalogs[pc] ?? .null,agent:agent)}
    private func requireNativeAgent(_ pc:UUID,agent:String,history:Bool=false) throws{
        try AgentBrand.requireNativeAgent(catalogs[pc] ?? .null,agent:agent,history:history)
    }
    private func saveAttachments() throws { try product.privateState.save(attachments.filter{!$0.value.isEmpty}, name: "ai-attachments") }
    private func uploadCache() throws -> AIAttachmentCache {
        if let attachmentCache { return attachmentCache }
        let cache = try AIAttachmentCache.open(scope: product.privateState.scope)
        attachmentCache = cache
        return cache
    }
    private func cleanAttachmentCache(_ ids: [String]) {
        guard let cache = attachmentCache, !ids.isEmpty else { return }
        // Deletion can finish after navigation/sign-out: the captured cache is
        // already bound to the original account and only these random IDs.
        Task { for id in ids { try? await cache.remove(id) } }
    }
    func reset(){epoch=UUID();catalogs=[:];conversations=[:];opened=[];uncertain=[:];selected=nil;drafts=[:];references=[:];attachments=[:];attachmentCache=nil;queued=[:];pinned=[];busy=[];unavailable=[];reading=[];preparingUploads=[];polling=false;externalLink=nil;directoryLocation=nil;exportURL=nil}
    func markUnavailable(_ pc:UUID){unavailable.insert(pc);conversations=conversations.filter{$0.value.pcID != pc}}
    func invalidate(){
        for session in Array(attachments.keys) {
            attachments[session] = attachments[session]?.map { item in
                var next=item;if next.status=="uploading"{next.status="paused";next.error="连接已暂停，请重新确认会话后重试。"};return next
            }
        }
        persist();epoch=UUID();unavailable=Set(product.pcs.map(\.id));conversations=[:];reading=[];busy=[];preparingUploads=[];polling=false
    }
    func refreshAll() async {
        guard !polling,product.relay?.state == .connected else{return};let owner=epoch;polling=true;defer{if owner==epoch{polling=false}}
        for pc in product.pcs where pc.online && pc.routing && pc.capabilities.contains("ai_agent_v1"){
            do{try await refresh(pc.id);if let selected,let current=conversations[selected],current.pcID==pc.id,!requiresHostUpgrade(pc.id,agent:current.raw["agentId"].string){try await refresh(pc.id,session:selected)}}catch is CancellationError{return}catch{if owner==epoch{unavailable.insert(pc.id)}}
        }
    }
    func refresh(_ pc:UUID,session:String?=nil) async throws {
        guard let relay=product.relay else{throw ClientError.readOnly};let owner=epoch
        if let session,session.hasPrefix("remote:") || conversations[session].map({requiresHostUpgrade(pc,agent:$0.raw["agentId"].string)})==true{throw ClientError.readOnly}
        let slot=pc.uuidString+":"+(session ?? "catalog");guard !reading.contains(slot) else{return};reading.insert(slot);defer{if owner==epoch{reading.remove(slot)}}
        var body:[String:JSONValue]=["action":.string("catalog")]
        if let session,let host=catalogs[pc]?["hostId"].string,!host.isEmpty{body=["action":.string("poll"),"hostId":.string(host),"sessionId":.string(session)]}
        let value=try await relay.ai(pc:pc,body:.object(body));try check(value);guard owner==epoch else{throw CancellationError()}
        if body["action"] == .string("catalog") {
            guard UUID(uuidString:value["hostId"].string) != nil,value["agents"].array.count<=16,value["sessions"].array.count<=64 else{throw ClientError.invalidResponse("AI catalog")}
            if let old=catalogs[pc],old["hostId"] != value["hostId"]{
                conversations=conversations.filter{$0.value.pcID != pc}
                for session in Array(attachments.keys) {
                    attachments[session] = attachments[session]?.map { item in
                        var next=item;if item.pcID==pc{next.status="failed";next.error="电脑已重启，附件仍保留在本机；请移除后重新选择。"};return next
                    }
                }
                persist()
            }
            let visible=Set(value["sessions"].array.map{$0["id"].string})
            conversations=conversations.filter{$0.value.pcID != pc || visible.contains($0.key)}
            catalogs[pc]=AIConfiguration.catalog(value);for s in value["sessions"].array{merge(s,pc:pc,metadata:true)}
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
        conversations[raw["id"].string] = .init(raw:AIConfiguration.conversation(.object(object)),pcID:pc)
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
        if let session,requiresHostUpgrade(pc,agent:session.raw["agentId"].string){throw ClientError.unavailable("请升级电脑端以使用 ZCode 原生会话。")}
        if ["create","import-native"].contains(action){try requireNativeAgent(pc,agent:extra["agentId"]?.string ?? "",history:action=="import-native")}
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
        if ["create","import-native"].contains(mutation.body["action"].string){try requireNativeAgent(pc,agent:mutation.body["agentId"].string,history:mutation.body["action"].string=="import-native")}
        let sessionID=mutation.body["sessionId"].string,agent=conversations[sessionID]?.raw["agentId"].string ?? opened.first(where:{$0.id==sessionID})?.agentID ?? mutation.body["agentId"].string
        if requiresHostUpgrade(pc,agent:agent){throw ClientError.unavailable("请升级电脑端以使用 ZCode 原生会话。")}
        let owner=epoch;busy.insert(pc);defer{if owner==epoch{busy.remove(pc)}}
        // Retry exactly the original host-bound operation ID and body, never a new mutation.
        let result=try await relay.ai(pc:pc,body:mutation.body)
        guard owner==epoch,result["hostId"]==mutation.body["hostId"] else{throw ClientError.readOnly}
        uncertain.removeValue(forKey:pc);persist();try check(result);if result["session"] != .null{merge(result["session"],pc:pc)}
        let session=mutation.body["sessionId"].string,queue=mutation.body["queueId"].string
        if !queue.isEmpty{queued[session]?.removeAll{$0.id==queue}}
        else if mutation.body["action"].string=="send"{applySendAcknowledgement(session,request:mutation.body)}
        if mutation.body["action"].string=="end"{closeView(session)}
        if mutation.body["action"].string=="forget"{
            guard result["deletedSessionId"].string==session else{throw ClientError.protocolViolation("deleted AI session")}
            let cachedIDs=(attachments[session] ?? []).map(\.id)
            conversations.removeValue(forKey:session);attachments.removeValue(forKey:session);queued.removeValue(forKey:session);drafts.removeValue(forKey:session);references.removeValue(forKey:session);pinned.remove(session);closeView(session)
            cleanAttachmentCache(cachedIDs)
        }
        persist()
        if ["create","fork","import-native"].contains(mutation.body["action"].string),let created=conversations[result["session"]["id"].string]{open(created)}
    }
    func create(pc:UUID,agent:String,directory:String,options:[String:JSONValue]=[:]) async throws {
        try requireNativeAgent(pc,agent:agent)
        let allowed=Set(["directoryMode","permissionMode","modelId","providerId","reasoningEffort","confirmedPermissionMode"])
        guard options.keys.allSatisfy({allowed.contains($0)}) else{throw ClientError.readOnly}
        var extra=options;extra["agentId"] = .string(agent);extra["directory"] = .string(directory)
        let value=try await mutate(pc:pc,action:"create",extra:extra)
        guard let s=conversations[value["session"]["id"].string] else{throw ClientError.invalidResponse("AI session")};open(s)
    }
    func send(_ s:AIConversation) async throws {
        let text=drafts[s.id] ?? "",files=attachments[s.id] ?? [],fileReferences=references[s.id] ?? []
        guard try AIMessageContent.hasContent(text:text,attachmentCount:files.count,fileReferences:fileReferences) else{return}
        guard files.allSatisfy({$0.status=="ready" && $0.isBound(to:s,host:catalogs[s.pcID]?["hostId"].string ?? "")})else{throw ClientError.readOnly}
        let request=JSONValue.object(["action":.string("send"),"text":.string(text),"attachmentIds":.array(files.map{.string($0.id)}),"fileReferences":.array(fileReferences.map(JSONValue.string))])
        _=try await mutate(pc:s.pcID,action:"send",session:s,extra:request.object)
        applySendAcknowledgement(s.id,request:request);persist()
    }
    private func applySendAcknowledgement(_ session:String,request:JSONValue) {
        let remaining=AIMessageContent.remainingAfterAcknowledgement(request:request,draft:drafts[session],
            attachmentIDs:(attachments[session] ?? []).map(\.id),fileReferences:references[session] ?? [])
        let retainedIDs=Set(remaining.attachmentIDs)
        let removed=(attachments[session] ?? []).filter{!retainedIDs.contains($0.id)}.map(\.id)
        attachments[session]?.removeAll{!retainedIDs.contains($0.id)}
        references[session]=remaining.fileReferences;drafts[session]=remaining.draft
        do{try saveAttachments();cleanAttachmentCache(removed)}catch{product.report(error)}
    }
    func read(_ s:AIConversation,action:String,extra:[String:JSONValue]=[:]) async throws -> JSONValue {
        if requiresHostUpgrade(s.pcID,agent:s.raw["agentId"].string){throw ClientError.unavailable("请升级电脑端以使用 ZCode 原生会话。")}
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
        conversations[s.id] = .init(raw:AIConfiguration.conversation(.object(object)),pcID:s.pcID)
    }
    func nativeHistory(pc:UUID,agent:String,cursor:String="") async throws -> JSONValue {
        try requireNativeAgent(pc,agent:agent,history:true)
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
        var extra=try AIModelConfiguration.fields(for:current,model:model,provider:provider,effort:effort)
        if let mode{guard config["permissionSwitch"].bool,let option=config["permissionModes"].array.first(where:{$0["id"].string==mode}),option["requiresConfirmation"] == .bool(false) || mode==current.raw["permissionMode"].string || confirmed==mode else{throw ClientError.readOnly}}
        extra["permissionMode"] = .string(mode ?? current.raw["permissionMode"].string);extra["expectedSettingsVersion"] = current.raw["settingsVersion"]
        if let confirmed{extra["confirmedPermissionMode"] = .string(confirmed)}
        _=try await mutate(pc:s.pcID,action:"configure",session:s,extra:extra)
    }
    private func validateModels(_ value:JSONValue,agent:String) throws{
        guard value["agentId"].string==agent,value["models"].array.count<=200 else{throw ClientError.invalidResponse("AI models")}
        for model in value["models"].array{let id=model["modelId"].string,provider=model["providerId"].string;guard !id.isEmpty,id.count<=200,provider.count<=200,!((id+provider).unicodeScalars.contains{CharacterSet.controlCharacters.contains($0)}),agent != "zcode" || !provider.isEmpty else{throw ClientError.invalidResponse("AI model identity")}}
    }
    func models(_ s:AIConversation) async throws->[JSONValue]{let value=try await read(s,action:"models",extra:["agentId":s.raw["agentId"]]);try validateModels(value,agent:s.raw["agentId"].string);return value["models"].array}
    func modelCatalog(pc:UUID,agent:String) async throws->JSONValue {
        try requireNativeAgent(pc,agent:agent)
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
        let cachedIDs=(attachments[s.id] ?? []).map(\.id)
        conversations.removeValue(forKey:s.id);attachments.removeValue(forKey:s.id);queued.removeValue(forKey:s.id);drafts.removeValue(forKey:s.id);references.removeValue(forKey:s.id);pinned.remove(s.id);closeView(s.id);cleanAttachmentCache(cachedIDs)
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
    private func attachmentContext(_ session: AIConversation, record: Attachment? = nil) throws -> (AIConversation, String) {
        if requiresHostUpgrade(session.pcID,agent:session.raw["agentId"].string){throw ClientError.unavailable("请升级电脑端以使用 ZCode 原生会话。")}
        guard product.active, product.signedIn, product.relay?.state == .connected,
              !unavailable.contains(session.pcID), !busy.contains(session.pcID), uncertain[session.pcID] == nil,
              product.pcs.contains(where: { $0.id == session.pcID && $0.online && $0.routing }),
              let current=conversations[session.id], current.pcID==session.pcID,
              current.raw["controlVersion"]==session.raw["controlVersion"], current.canControl,
              current.capabilities["attachments"].bool, let host=catalogs[session.pcID]?["hostId"].string,
              UUID(uuidString:host) != nil, (1...24576).contains(current.capabilities["chunkBytes"].integer) else { throw ClientError.readOnly }
        if let record, !record.isBound(to: current, host: host) {
            throw ClientError.unavailable("附件所属主机或控制版本已变化，请移除后重新选择。")
        }
        return (current, host)
    }
    private func checkAttachmentCapacity(_ session: AIConversation, size: Int64, excluding id: String? = nil) throws {
        let items=(attachments[session.id] ?? []).filter{$0.id != id}
        let capabilities=session.capabilities
        guard size>0, size<=min(AIAttachmentCache.maxFileBytes,capabilities["maxFileBytes"].integer),
              attachments[session.id]?.isEmpty == false || attachments.values.filter({!$0.isEmpty}).count<64,
              items.count<min(5,Int(capabilities["maxFiles"].integer)),
              items.reduce(size,{$0+$1.size})<=min(50*1024*1024,capabilities["maxTotalBytes"].integer) else { throw ClientError.limitExceeded }
    }
    func upload(_ session: AIConversation, url: URL) async throws {
        guard !preparingUploads.contains(session.id) else { throw ClientError.readOnly }
        let (current,host)=try attachmentContext(session), owner=epoch
        preparingUploads.insert(session.id);defer{if owner==epoch{preparingUploads.remove(session.id)}}
        let access=url.startAccessingSecurityScopedResource();defer{if access{url.stopAccessingSecurityScopedResource()}}
        let values=try url.resourceValues(forKeys:[.fileSizeKey,.contentTypeKey,.isRegularFileKey,.isSymbolicLinkKey])
        guard values.isRegularFile==true,values.isSymbolicLink != true,let size=values.fileSize else { throw ClientError.invalidResponse("attachment file") }
        try checkAttachmentCapacity(current,size:Int64(size))
        let name=try Attachment.fileName(url.lastPathComponent)
        let id=UUID().uuidString.lowercased(),cache=try uploadCache()
        var retained=false
        do {
            let digest=try await cache.stage(url,id:id,expectedSize:Int64(size))
            guard owner==epoch else{throw CancellationError()}
            _=try attachmentContext(session)
            let record=Attachment(id:id,pcID:session.pcID,sessionID:session.id,host:host,
                controlVersion:current.raw["controlVersion"],name:name,size:Int64(size),
                mime:values.contentType?.preferredMIMEType ?? "application/octet-stream",digest:digest,sent:0,status:"paused")
            guard record.validMetadata else{throw ClientError.invalidResponse("attachment metadata")}
            var next=attachments;next[session.id,default:[]].append(record)
            try product.privateState.save(next,name:"ai-attachments");attachments=next;retained=true
            try await performUpload(session,record:record,cache:cache,owner:owner)
        } catch {
            if !retained{try? await cache.remove(id)}
            throw error
        }
    }
    func retryUpload(_ session: AIConversation, id: String) async throws {
        guard !preparingUploads.contains(session.id),let record=attachments[session.id]?.first(where:{$0.id==id}),record.status != "uploading" else { throw ClientError.readOnly }
        let (current,_)=try attachmentContext(session,record:record)
        try checkAttachmentCapacity(current,size:record.size,excluding:id)
        let cache=try uploadCache(),owner=epoch
        preparingUploads.insert(session.id);defer{if owner==epoch{preparingUploads.remove(session.id)}}
        try await performUpload(session,record:record,cache:cache,owner:owner)
    }
    func canRetryUpload(_ session: AIConversation, id: String) -> Bool {
        guard !preparingUploads.contains(session.id),let record=attachments[session.id]?.first(where:{$0.id==id}),
              record.status != "uploading",record.status != "ready" else{return false}
        return (try? attachmentContext(session,record:record)) != nil
    }
    private func performUpload(_ session: AIConversation, record: Attachment, cache: AIAttachmentCache, owner: UUID) async throws {
        var entry=record
        do {
            entry.status="uploading";entry.error=nil
            try updateAttachment(entry,persist:true)
            let bytes=try await cache.read(record)
            guard owner==epoch else{throw CancellationError()}
            let (current,_)=try attachmentContext(session,record:record)
            guard let relay=product.relay else{throw ClientError.readOnly}
            let finished=try await AIAttachmentTransfer.upload(entry,bytes:bytes,chunkBytes:Int(current.capabilities["chunkBytes"].integer),request:{action,extra in
                guard self.epoch==owner else{throw CancellationError()}
                _=try self.attachmentContext(session,record:record)
                var body=extra;body["action"] = .string(action);body["hostId"] = .string(record.host)
                body["sessionId"] = .string(record.sessionID);body["controlVersion"] = record.controlVersion;body["attachmentId"] = .string(record.id)
                let reply=try await relay.ai(pc:record.pcID,body:.object(body))
                guard self.epoch==owner else{throw CancellationError()}
                _=try self.attachmentContext(session,record:record)
                guard reply["hostId"].string==record.host else{throw ClientError.readOnly}
                try self.check(reply);return reply
            },progress:{value in
                guard self.epoch==owner else{return};entry=value
                if value.status != "ready"{try? self.updateAttachment(value,persist:false)}
            })
            guard owner==epoch else{throw CancellationError()}
            try updateAttachment(finished,persist:true)
        } catch {
            if owner==epoch {
                entry.status=error is CancellationError ? "paused":"failed"
                entry.error=String(error.localizedDescription.prefix(240))
                do{try updateAttachment(entry,persist:true)}catch{product.report(error)}
            }
            throw error
        }
    }
    private func updateAttachment(_ entry: Attachment, persist: Bool) throws {
        guard let index=attachments[entry.sessionID]?.firstIndex(where:{$0.id==entry.id}) else{throw CancellationError()}
        if persist {
            var next=attachments;next[entry.sessionID]?[index]=entry
            try product.privateState.save(next,name:"ai-attachments");attachments=next
        } else { attachments[entry.sessionID]?[index]=entry }
    }
    func removeAttachment(_ session: AIConversation, id: String) async throws {
        guard let entry=attachments[session.id]?.first(where:{$0.id==id}) else{return}
        guard entry.status != "uploading",!busy.contains(session.pcID),uncertain[session.pcID]==nil else{throw ClientError.readOnly}
        let owner=epoch
        if entry.isBound(to:session,host:catalogs[session.pcID]?["hostId"].string ?? ""),product.relay?.state == .connected {
            _=try await read(session,action:"attachment.remove",extra:["attachmentId":.string(id),"controlVersion":entry.controlVersion])
        }
        guard owner==epoch else{throw CancellationError()}
        var next=attachments;next[session.id]?.removeAll{$0.id==id}
        try product.privateState.save(next,name:"ai-attachments");attachments=next
        cleanAttachmentCache([id])
    }
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
