// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import Foundation
import Combine

@MainActor final class TerminalController: ObservableObject {
    @Published private(set) var presentation = TerminalPresentation()
    var frame: TerminalFrame? { presentation.frame }
    var frozen: Bool { presentation.frozen }
    var isSynchronized: Bool { attached && !closed && snapshotID == nil && frame != nil }
    @Published private(set) var status = "状态待确认"
    @Published private(set) var error: String?
    @Published private(set) var canInput = false
    @Published private(set) var controlNotice: String?
    private var controlRequest: Task<Void, Error>?
    private var automaticControl: Task<Void, Never>?
    private var controlGeneration = UUID()
    private var automaticControlPending = false
    private var ended = false
    let session:TerminalSession
    private let pc:PC,relay:RelayClient,api:APIClient,identity:DeviceIdentity,state:SecureState
    private var core:TLTerminalBridge?,ack:UInt64=0,inputSequence:UInt64=0,localInputEpoch:UInt64=0
    private var lease:UUID?,leaseVersion:UInt64=0,leaseExpiry=Date.distantPast
    private var leaseUsable = false
    private var renewal:Task<Void,Never>?,deadline:Task<Void,Never>?,resizeTask:Task<Void,Never>?,attachID=UUID(),attached=false,closed=false
    private var viewport:(Int,Int,Int,Int)?
    private var snapshotID:UUID?,snapshotObject:UUID?,snapshotBytes=Data(),snapshotHash=Data(),snapshotCount:UInt64=0,snapshotIndex:UInt64=0,snapshotSize:UInt64=0
    private var bootstrap:[RelayClient.Opened]=[]
    private var incarnation=UUID()
    private var historyDeadline: Task<Void, Never>?
    weak var activity:ActivityStore?
    private var title:String{session.title.isEmpty ? session.shell:session.title}
    init(session:TerminalSession,pc:PC,relay:RelayClient,api:APIClient,identity:DeviceIdentity,state:SecureState){self.session=session;self.pc=pc;self.relay=relay;self.api=api;self.identity=identity;self.state=state}
    func present(_ error:Error) { if !(error is CancellationError) { self.error=error.localizedDescription } }
    func open(automaticallyRequestControl: Bool = true) async throws {
        automaticControlPending = automaticallyRequestControl && session.lifecycle == "RUNNING"
        ended = session.lifecycle != "RUNNING"
        let owner=incarnation
        guard TLTerminalBridge.available else{throw ClientError.unavailable("缺少 iOS terminal-core XCFramework；终端功能未启用。")}
        core=try TLTerminalBridge(columns:UInt32(session.columns),rows:UInt32(session.rows),sequence:1)
        guard core != nil else{throw ClientError.unavailable("无法创建原生终端。")}
        // Local snapshot restore is display-only; the host snapshot below stays authoritative,
        // so the stored ack is never used as the subscribe resume point.
        if let persisted=try? state.load(PersistedTerminalSnapshot.storageName(session.id),as:PersistedTerminalSnapshot.self),persisted.version==1,!persisted.snapshot.isEmpty{
            do{try core?.importSnapshot(persisted.snapshot);localInputEpoch=max(localInputEpoch,persisted.localInputEpoch);if let raw=try core?.renderFrame(){presentation.updateLive(try TerminalFrame.decode(raw))}}
            catch{core=try TLTerminalBridge(columns:UInt32(session.columns),rows:UInt32(session.rows),sequence:1)}
        }
        try relay.bindSession(session.id,pcID:pc.id){[weak self] event in try await self?.receive(event)}
        _=try await relay.send(route:session.id,type:.unsubscribe,payload:ProtoMessage())
        _=try await api.request("/v1/sessions/\(session.id)/subscribe",method:"POST",body:.object([:]),idempotency:UUID())
        guard owner==incarnation,!closed else{throw CancellationError()}
        var payload=ProtoMessage();payload.set(1,ack);payload.set(2,try identity.publicExchange);payload.set(4,attachID.bytes)
        // Advertise only implemented decoder contracts. Graphics/Unicode16 require their own native frame adapters.
        for c in ["terminal_attach_v1","terminal_state_2_21_v2","terminal_host_geometry_v1","terminal_host_theme_v1","terminal_host_resize_v1"]{payload.add(3,Data(c.utf8))}
        _=try await relay.send(route:session.id,type:.subscribe,payload:payload)
        status="正在确认终端协议"
        deadline=Task{[weak self] in do{try await Task.sleep(for:.seconds(15));guard let self,self.incarnation==owner,!self.attached else{return};self.invalidate("主机未及时确认终端协议，请重新打开会话。")}catch{}}
    }
    func invalidate(_ reason:String){cancelControlRequest();canInput=false;leaseUsable=false;lease=nil;attached=false;presentation.reset(core:core);historyDeadline?.cancel();bootstrap=[];snapshotID=nil;snapshotBytes=Data();renewal?.cancel();deadline?.cancel();resizeTask?.cancel();status="状态待确认";error=reason;incarnation=UUID()}
    func close(){guard !closed else{return};let held=lease;closed=true;invalidate("会话视图已关闭。");relay.unbindSession(session.id);core=nil
        if let held{let api=api,id=session.id;Task{_=try? await api.request("/v1/sessions/\(id)/lease/\(held)",method:"DELETE",idempotency:UUID())}}
    }
    func browseHistory(by rows: Int) {
        guard attached, !closed, snapshotID == nil, let core, presentation.liveFrame != nil else { return }
        do {
            if !frozen {
                resizeTask?.cancel()
                try presentation.begin(core: core)
                canInput = false
                startHistoryDeadline()
            }
            try presentation.scroll(core: core, by: rows)
        } catch {
            endHistory()
            present(ClientError.unavailable("历史画面已失效，已返回实时尾部。"))
        }
    }
    func endHistory(restoreInput: Bool = true) {
        historyDeadline?.cancel(); historyDeadline = nil
        presentation.end(core: core)
        canInput = restoreInput && leaseUsable && attached && !closed && snapshotID == nil && lease != nil && Date() < leaseExpiry && relay.state == .connected
        if restoreInput { startAutomaticControlIfReady() }
    }
    private func startHistoryDeadline() {
        historyDeadline?.cancel()
        let owner = incarnation
        historyDeadline = Task { [weak self] in
            do {
                // Native captures expire after five minutes. Release before expiry.
                try await Task.sleep(for: .seconds(290))
                guard let self, self.incarnation == owner else { return }
                self.endHistory()
            } catch {}
        }
    }
    func shellState() throws -> TerminalShellState {
        guard attached, !closed, snapshotID == nil, let core,
              let sequence = presentation.liveFrame?.nextSequence else { throw ClientError.readOnly }
        return try TerminalShellState.decode(core.shellState(), nextSequence: sequence)
    }
    func showShellCommand(_ command: TerminalShellCommand) throws {
        guard let current = try shellState().command(matching: command),
              !current.prompt.alternate, current.prompt.page == 1,
              attached, !closed, snapshotID == nil, let core else { throw ClientError.readOnly }
        endHistory()
        do {
            resizeTask?.cancel()
            try presentation.begin(core: core)
            canInput = false
            let offset = try core.historyOffset(forLine: current.prompt.lineID, cellOffset: current.prompt.cellOffset)
            try presentation.show(core: core, offset: offset.intValue)
            startHistoryDeadline()
        } catch { endHistory(); throw error }
    }
    private func leaseDate(_ value:String)->Date? {
        let formatter=ISO8601DateFormatter();formatter.formatOptions=[.withInternetDateTime,.withFractionalSeconds]
        return formatter.date(from:value) ?? ISO8601DateFormatter().date(from:value)
    }
    func requestAutomaticControl() {
        guard !ended, !closed else { return }
        automaticControlPending = true
        startAutomaticControlIfReady()
    }
    func cancelAutomaticControl() {
        automaticControlPending = false
        automaticControl?.cancel(); automaticControl = nil
        cancelControlRequest()
    }
    private func startAutomaticControlIfReady() {
        guard automaticControlPending, isSynchronized, !frozen, !ended, automaticControl == nil else { return }
        automaticControlPending = false
        let owner = incarnation
        automaticControl = Task { [weak self] in
            guard let self else { return }
            defer {
                if self.incarnation == owner {
                    self.automaticControl = nil
                    self.startAutomaticControlIfReady()
                }
            }
            do { try await self.requestControl() }
            catch is CancellationError { }
            catch {
                guard self.incarnation == owner, !self.closed, !self.ended else { return }
                self.canInput = false; self.status = "只读"
                if let api = error as? APIError, api.status == 409, api.code == "LEASE_HELD" {
                    self.controlNotice = "控制权仍被占用；当前保留只读画面，可稍后重试。"
                } else if let api = error as? APIError, api.status == 403 {
                    self.controlNotice = "当前设备没有控制权限，已保留只读画面。"
                } else { self.controlNotice = "未能取得控制权，已保留画面；可在菜单中重试。" }
            }
        }
    }
    private func cancelControlRequest() {
        controlGeneration = UUID()
        controlRequest?.cancel(); controlRequest = nil
        automaticControlPending = false
        controlNotice = nil
        if !leaseUsable && attached && !ended { status = "只读" }
    }
    func requestControl() async throws {
        guard isSynchronized, !frozen, !ended, relay.state == .connected else { throw ClientError.readOnly }
        if leaseUsable, lease != nil, Date() < leaseExpiry { canInput = true; return }
        if let request = controlRequest { try await request.value; return }
        let owner = incarnation, generation = UUID(), inputEpoch = localInputEpoch
        controlGeneration = generation
        controlNotice = nil; status = "正在获取控制权"
        let request = Task { [weak self] in
            guard let self else { throw CancellationError() }
            try await TerminalControlAcquisition.acquire(current: {
                self.incarnation == owner && self.controlGeneration == generation && !self.closed && !self.ended &&
                    self.isSynchronized && !self.frozen && self.relay.state == .connected && self.localInputEpoch == inputEpoch
            }, controlling: { self.leaseUsable && self.lease != nil && Date() < self.leaseExpiry }, request: {
                try await self.acquireLease(owner: owner, generation: generation, inputEpoch: inputEpoch)
            }, retrying: { self.status = "等待控制权" })
        }
        controlRequest = request
        defer { if controlGeneration == generation { controlRequest = nil } }
        try await withTaskCancellationHandler(operation: { try await request.value }, onCancel: { request.cancel() })
    }
    private func acquireLease(owner: UUID, generation: UUID, inputEpoch: UInt64) async throws {
        let result = try await api.request("/v1/sessions/\(session.id)/lease", method: "POST", body: .object([:]), idempotency: UUID())
        try Task.checkCancellation()
        guard owner == incarnation, controlGeneration == generation, localInputEpoch == inputEpoch,
              isSynchronized, !closed, !frozen, !ended, relay.state == .connected else { throw CancellationError() }
        let grant = try TerminalLeaseGrant.decode(result)
        let id = grant.id
        lease = id; leaseVersion = grant.version; leaseExpiry = grant.expires; leaseUsable = true
        canInput = true; controlNotice = nil; status = "控制中"
        activity?.record("lease-granted", "获得了「\(title)」的控制权")
        if let viewport { fit(columns: viewport.0, rows: viewport.1, cellWidth: viewport.2, cellHeight: viewport.3) }
        renewal?.cancel();renewal=Task{[weak self] in
            while !Task.isCancelled {do{try await Task.sleep(for:.seconds(10));guard let self,self.incarnation==owner,self.lease==id else{return}
                let result=try await self.api.request("/v1/sessions/\(self.session.id)/lease/\(id)/renew",method:"POST",body:.object([:]),idempotency:UUID())
                guard self.incarnation==owner,self.lease==id,let expiry=self.leaseDate(result["expires_at"].string),expiry>Date() else{throw ClientError.readOnly};self.leaseExpiry=expiry
            }catch{if self?.incarnation==owner,self?.lease==id{self?.canInput=false;self?.leaseUsable=false;self?.status="只读"};return}}
        }
    }
    func releaseControl() async throws{cancelAutomaticControl();guard let id=lease else{return};canInput=false;leaseUsable=false;lease=nil;renewal?.cancel();status="只读";_=try await api.request("/v1/sessions/\(session.id)/lease/\(id)",method:"DELETE",idempotency:UUID());activity?.record("lease-released","释放了「\(title)」的控制权")}
    func text(_ text:String) async throws {
        guard !text.isEmpty,text.utf8.count<=65536 else{throw ClientError.limitExceeded}
        var data=Data()
        for scalar in text.unicodeScalars {
            let key:UInt32=scalar.value==10 || scalar.value==13 ? 2:1
            guard let encoded=try core?.encodeKey(key,scalar:key==1 ? scalar.value:0,modifiers:0,paste:nil) else{throw ClientError.readOnly};data.append(encoded)
        }
        try await input(data,typedKind:4)
    }
    func key(_ key:UInt32,modifiers:UInt32=0) async throws {
        guard let data=try core?.encodeKey(key,scalar:0,modifiers:modifiers,paste:nil) else{throw ClientError.readOnly}
        // RAW_BYTES preserves the core's negotiated input encoding, including application cursor modes.
        try await input(data,typedKind:4)
    }
    /// Custom dock shortcut: quick input goes through the text path, combinations through the
    /// core's key encoder with the stored canonical modifiers (mirrors Android dispatchTerminalCustomShortcut).
    func dispatch(_ shortcut:TerminalCustomShortcut) async throws {
        if shortcut.kind == .quickInput { try await text(shortcut.text) }
        else {
            guard let data=try core?.encodeKey(shortcut.key,scalar:shortcut.key==1 ? shortcut.scalar:0,modifiers:shortcut.modifiers,paste:nil) else{throw ClientError.readOnly}
            try await input(data,typedKind:4)
        }
    }
    func paste(_ data:Data) async throws{guard data.count<=65536,let encoded=try core?.encodeKey(0,scalar:0,modifiers:0,paste:data) else{throw ClientError.limitExceeded};try await input(encoded,typedKind:4)}
    private func input(_ data:Data,typedKind:UInt64,text:String="") async throws {
        guard canInput,!frozen,attached,let lease,Date()<leaseExpiry,relay.state == .connected else{throw ClientError.readOnly}
        inputSequence+=1;var p=ProtoMessage();p.set(1,UUID().bytes);p.set(2,lease.bytes);p.set(3,leaseVersion);p.set(4,inputSequence);p.set(5,data);p.set(6,localInputEpoch)
        var typed=ProtoMessage();typed.set(1,UInt64(1));typed.set(2,typedKind);if typedKind==1{typed.set(3,text)}else{typed.set(6,data)};p.set(7,typed.encoded())
        _=try await relay.send(route:session.id,type:.input,payload:p)
    }
    /// Explicit window resize mirroring Android `requestWindowResize`: measured metrics apply a
    /// phone-sized grid, `restore:true` (ResizePayload.restore = 8, zeroed dimensions) asks the
    /// host to put back its own desktop window size. No blind retries.
    func resize(columns:Int,rows:Int,cellWidth:Int,cellHeight:Int,restore:Bool=false) async throws ->(columns:Int,rows:Int){
        guard canInput,!frozen,let lease,restore || ((2...1000).contains(columns)&&(2...1000).contains(rows)) else{throw ClientError.readOnly}
        let result=try await relay.request(route:session.id,type:.resize,response:.resizeResponse,capability:"terminal_host_resize_v1",timeout:.seconds(12)){id in
            var p=ProtoMessage();if !restore{p.set(1,UInt64(columns));p.set(2,UInt64(rows));p.set(3,UInt64(cellWidth));p.set(4,UInt64(cellHeight))};p.set(5,lease.bytes);p.set(6,leaseVersion);p.set(7,id.bytes);if restore{p.set(8,UInt64(1))};p.set(9,localInputEpoch);p.set(10,UInt64(Date().timeIntervalSince1970*1000)+10000);return p
        }
        let reason=try result.text(4);guard reason.isEmpty else{throw ClientError.unavailable(TerminalWindowSize.message(for:reason))}
        guard result.uint(2)>0,result.uint(3)>0 else{throw ClientError.unavailable(TerminalWindowSize.message(for:"WINDOW_RESIZE_UNSUPPORTED"))}
        if !restore{guard result.uint(2)==UInt64(columns),result.uint(3)==UInt64(rows) else{throw ClientError.unavailable(TerminalWindowSize.message(for:"WINDOW_RESIZE_UNSUPPORTED"))}}
        return(Int(result.uint(2)),Int(result.uint(3)))
    }
    func fitToPhone() async throws ->(columns:Int,rows:Int){
        guard pc.capabilities.contains("terminal_window_resize_v1") else{throw ClientError.unavailable(TerminalWindowSize.message(for:"RESIZE_UNSUPPORTED"))}
        guard let viewport else{throw ClientError.unavailable("终端可视区域尚未就绪。")}
        return try await resize(columns:viewport.0,rows:viewport.1,cellWidth:viewport.2,cellHeight:viewport.3)
    }
    func restorePCWindow() async throws ->(columns:Int,rows:Int){
        guard pc.capabilities.contains("terminal_window_resize_v1") else{throw ClientError.unavailable(TerminalWindowSize.message(for:"RESIZE_UNSUPPORTED"))}
        return try await resize(columns:0,rows:0,cellWidth:0,cellHeight:0,restore:true)
    }
    func fit(columns:Int,rows:Int,cellWidth:Int,cellHeight:Int){
        let desired=(min(512,max(2,columns)),min(512,max(2,rows)),max(1,cellWidth),max(1,cellHeight));viewport=desired
        guard canInput,!frozen,attached,pc.capabilities.contains("terminal_window_resize_v1"),frame?.columns != desired.0 || frame?.rows != desired.1 else{return}
        resizeTask?.cancel();let owner=incarnation
        resizeTask=Task{[weak self] in do{try await Task.sleep(for:.milliseconds(200));guard let self,self.incarnation==owner else{return};_=try await self.resize(columns:desired.0,rows:desired.1,cellWidth:desired.2,cellHeight:desired.3)}catch is CancellationError{}catch{if self?.incarnation==owner{self?.present(error)}}}
    }
    func terminate() async throws {
        let p=try await relay.request(route:session.id,type:.terminate,response:.terminateResponse,capability:"host_history_v1"){id in var p=ProtoMessage();p.set(1,id.bytes);p.set(2,"USER_FORCE_CLOSE");return p}
        guard p.uint(2)==1,try p.text(3).isEmpty else{throw ClientError.unavailable("电脑尚未确认结束会话。")}
    }
    private func receive(_ event:RelayClient.Opened) async throws {
        guard !closed else{return};let p=event.payload
        if event.type == .attachResult {
            guard p.bytes(1)==attachID.bytes,try p.text(3).isEmpty,try p.text(2)=="terminal_state_2_21_v2" else{invalidate("此会话需要尚未接入的 iOS 终端图形／Unicode16 核心能力。");return}
            deadline?.cancel();attached=true;status="只读";try await requestSnapshot();return
        }
        if event.type == .historyResponse {try await historyChunk(p);return}
        if event.type == .leaseRevoked || event.type == .leaseDenied {cancelAutomaticControl();canInput=false;leaseUsable=false;lease=nil;status="只读";localInputEpoch=max(localInputEpoch,p.uint(5));if event.type == .leaseRevoked{activity?.record("lease-revoked","「\(title)」的控制权已被释放")};return}
        if event.type == .leaseGranted {return}
        guard attached else{return}
        guard [.output,.resizeApplied,.geometry,.hostTheme,.hostResize,.gap,.upsert,.removed,.theme,.started,.ended,.interrupted,.brokerOutput,.inputAccepted,.inputRejected].contains(event.type) else{return}
        if snapshotID != nil {guard bootstrap.count<128 else{throw ClientError.limitExceeded};bootstrap.append(event);return}
        guard event.sequence>ack else{return}
        guard event.sequence==ack+1 else{canInput=false;status="恢复历史中";try await requestSnapshot();return}
        do{
            switch event.type {
            case .output:try core?.applyData(p.bytes(1),kind:1,sequence:event.sequence,columns:0,rows:0)
            case .inputAccepted,.inputRejected:
                guard let core else{throw ClientError.readOnly}
                if let reason=try TerminalInputResult.apply(type:event.type,payload:p,sequence:event.sequence,to:core){
                    canInput=false;leaseUsable=false;lease=nil;renewal?.cancel();status="只读";error=reason
                }
            case .resizeApplied:
                guard (1...512).contains(p.uint(1)),(1...512).contains(p.uint(2)) else{throw ClientError.protocolViolation("resize dimensions")}
                try core?.applyData(Data(),kind:2,sequence:event.sequence,columns:UInt32(p.uint(1)),rows:UInt32(p.uint(2)))
            case .geometry:try core?.applyData(p.bytes(1),kind:5,sequence:event.sequence,columns:0,rows:0)
            case .hostTheme:try core?.applyData(p.bytes(1),kind:6,sequence:event.sequence,columns:0,rows:0)
            case .gap:canInput=false;try await requestSnapshot();return
            case .brokerOutput:invalidate("当前 iOS 终端未支持 Broker 图形捕获。");return
            case .ended,.interrupted:ended=true;cancelAutomaticControl();canInput=false;leaseUsable=false;lease=nil;status="已结束";fallthrough
            case .upsert,.removed,.theme,.started,.hostResize:try core?.applyData(Data(),kind:3,sequence:event.sequence,columns:0,rows:0)
            default:return
            }
            guard let raw=try core?.renderFrame() else{throw ClientError.readOnly};let next=try TerminalFrame.decode(raw)
            guard next.nextSequence==event.sequence+1 else{throw ClientError.protocolViolation("applied sequence")}
            // Never acknowledge bytes that the native core could not validate and apply.
            // Persist the validated native snapshot before advancing the durable event acknowledgement.
            guard let snapshot=try core?.exportSnapshot() else{throw ClientError.readOnly}
            try state.save(PersistedTerminalSnapshot(version:1,ack:event.sequence,localInputEpoch:localInputEpoch,snapshot:snapshot),name:PersistedTerminalSnapshot.storageName(session.id));presentation.updateLive(next);ack=event.sequence;try await acknowledge()
        }catch{invalidate("终端状态校验失败，已停止输入与 ACK。");throw error}
    }
    private func acknowledge() async throws{var p=ProtoMessage();p.set(1,ack);_=try await relay.send(route:session.id,type:.ack,payload:p,acknowledgement:ack)
        if relay.capabilities.contains("flow_control_v1"){var flow=ProtoMessage();flow.set(1,ack);flow.set(2,UInt64(32));flow.set(3,UInt64(3*1024*1024));flow.set(6,UInt64(1));_=try await relay.send(route:session.id,type:.flow,payload:flow,acknowledgement:ack)}
    }
    private func requestSnapshot() async throws {
        let resumeControl = automaticControlPending || controlRequest != nil || leaseUsable
        cancelControlRequest()
        automaticControlPending = resumeControl && !ended
        canInput = false; leaseUsable=false
        endHistory(restoreInput:false)
        let id=UUID();snapshotID=id;snapshotObject=nil;snapshotBytes=Data();snapshotIndex=0;bootstrap=[]
        var p=ProtoMessage();p.set(1,id.bytes);p.set(2,UInt64(1));p.add(8,Data("terminal_state_2_21_v2".utf8));_=try await relay.send(route:session.id,type:.historyRequest,payload:p)
        let owner=incarnation;deadline?.cancel();deadline=Task{[weak self] in do{try await Task.sleep(for:.seconds(90));guard let self,self.incarnation==owner,self.snapshotID==id else{return};self.invalidate("主机历史同步超时，请重新打开会话。")}catch{}}
    }
    private func historyChunk(_ p:ProtoMessage) async throws {
        guard let id=snapshotID,p.bytes(1)==id.bytes else{return}
        let owner=incarnation
        guard p.uint(2)==1,p.uint(3)==1,p.uint(21)>0,p.uint(21)<=64*1024*1024+4096,p.uint(22)==snapshotIndex,p.uint(23)>0,p.uint(23)<=129 else{invalidate("主机历史尚不可用。");return}
        let object=try UUID(bytes:p.bytes(4))
        if snapshotObject==nil{snapshotObject=object;snapshotHash=p.bytes(20);snapshotCount=p.uint(23);snapshotSize=p.uint(21)}
        guard snapshotObject==object,p.bytes(20)==snapshotHash,p.uint(23)==snapshotCount,p.uint(21)==snapshotSize,p.bytes(24).count<=512*1024,snapshotBytes.count+p.bytes(24).count<=Int(snapshotSize) else{throw ClientError.protocolViolation("snapshot chunks")}
        snapshotBytes.append(p.bytes(24));snapshotIndex+=1
        var control=ProtoMessage();control.set(1,id.bytes);control.set(2,UInt64(1));control.set(3,object.bytes);control.set(4,p.uint(22));_=try await relay.send(route:session.id,type:.historyControl,payload:control)
        guard owner==incarnation,!closed,snapshotID==id else{return}
        guard snapshotIndex==snapshotCount else{return}
        guard snapshotBytes.count==Int(snapshotSize),Data(SHA256.hash(data:snapshotBytes))==snapshotHash else{throw ClientError.protocolViolation("snapshot hash")}
        // The object is separately recipient-sealed even though every transport chunk was authenticated.
        let container=try ProtoMessage(snapshotBytes,maximum:64*1024*1024+4096)
        try TerminalSnapshotProtocol.validate(container,sessionID:session.id,pcDeviceID:pc.deviceID,
            recipientID:identity.id,keyEpoch:pc.keyEpoch,snapshotSequence:p.uint(5),minimumSequence:ack)
        guard let peer=try? await api.request("/v1/devices"),let device=try peer["items"].array.map(DeviceRecord.init).first(where:{$0.id==pc.deviceID}) else{throw ClientError.readOnly}
        guard owner==incarnation,!closed,snapshotID==id else{return}
        guard device.trust=="TRUSTED" else{throw ClientError.readOnly}
        let pair=try SessionCrypto.pairwise(identity:identity,peer:device.exchangeKey,pc:pc.deviceID,mobile:identity.id,epoch:pc.keyEpoch)
        let plain=try TerminalSnapshotProtocol.open(container,pairwise:pair,sessionID:session.id,pcDeviceID:pc.deviceID,
            recipientID:identity.id,keyEpoch:pc.keyEpoch,snapshotSequence:p.uint(5),minimumSequence:ack)
        try core?.importSnapshot(plain);guard let raw=try core?.renderFrame() else{throw ClientError.readOnly}
        let rendered=try TerminalFrame.decode(raw);guard rendered.nextSequence==container.uint(7)+1 else{throw ClientError.protocolViolation("snapshot sequence")}
        try state.save(PersistedTerminalSnapshot(version:1,ack:container.uint(7),localInputEpoch:container.uint(17),snapshot:plain),name:PersistedTerminalSnapshot.storageName(session.id))
        presentation.updateLive(rendered);ack=container.uint(7);localInputEpoch=container.uint(17);snapshotID=nil;snapshotBytes=Data();deadline?.cancel();try await acknowledge();activity?.record("snapshot-restored","「\(title)」历史缺口已恢复，终端画面已重建")
        let waiting=bootstrap;bootstrap=[];for event in waiting{try await receive(event)}
        startAutomaticControlIfReady()
    }
}

/// Input receipts consume the same authoritative source sequence as output.
/// Validate before advancing; the caller persists the snapshot before its ACK.
enum TerminalInputResult {
    static func apply(type: WireType, payload: ProtoMessage, sequence: UInt64,
                      to core: TLTerminalBridge) throws -> String? {
        guard type == .inputAccepted || type == .inputRejected else { throw ClientError.protocolViolation("input result type") }
        try payload.validateSingular([1,2,3])
        let id=payload.bytes(1),accepted=payload.uint(2),reason=try payload.text(3)
        guard payload.values(1).allSatisfy({$0.wire==2}),payload.values(2).allSatisfy({$0.wire==0}),
              payload.values(3).allSatisfy({$0.wire==2}),
              id.count==16,id.contains(where:{$0 != 0}),accepted==(type == .inputAccepted ? 1:0),
              reason.utf8.count<=1024,!reason.unicodeScalars.contains(where:{CharacterSet.controlCharacters.contains($0)}) else {
            throw ClientError.protocolViolation("input result")
        }
        try core.applyData(Data(),kind:3,sequence:sequence,columns:0,rows:0)
        return type == .inputRejected ? (reason.isEmpty ? "电脑拒绝了输入，请重新确认控制权。":reason):nil
    }
}
