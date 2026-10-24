// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import Foundation
import Combine

@MainActor final class TerminalController: ObservableObject {
    @Published private(set) var frame:TerminalFrame?,status="状态待确认",error:String?,canInput=false,frozen=false
    let session:TerminalSession
    private let pc:PC,relay:RelayClient,api:APIClient,identity:DeviceIdentity,state:SecureState
    private var core:TLTerminalBridge?,ack:UInt64=0,inputSequence:UInt64=0,localInputEpoch:UInt64=0
    private var lease:UUID?,leaseVersion:UInt64=0,leaseExpiry=Date.distantPast
    private var renewal:Task<Void,Never>?,deadline:Task<Void,Never>?,resizeTask:Task<Void,Never>?,attachID=UUID(),attached=false,closed=false
    private var viewport:(Int,Int,Int,Int)?
    private var snapshotID:UUID?,snapshotObject:UUID?,snapshotBytes=Data(),snapshotHash=Data(),snapshotCount:UInt64=0,snapshotIndex:UInt64=0,snapshotSize:UInt64=0
    private var bootstrap:[RelayClient.Opened]=[]
    private var incarnation=UUID()
    weak var activity:ActivityStore?
    private var title:String{session.title.isEmpty ? session.shell:session.title}
    init(session:TerminalSession,pc:PC,relay:RelayClient,api:APIClient,identity:DeviceIdentity,state:SecureState){self.session=session;self.pc=pc;self.relay=relay;self.api=api;self.identity=identity;self.state=state}
    func present(_ error:Error) { if !(error is CancellationError) { self.error=error.localizedDescription } }
    func open() async throws {
        let owner=incarnation
        guard TLTerminalBridge.available else{throw ClientError.unavailable("缺少 iOS terminal-core XCFramework；终端功能未启用。")}
        core=try TLTerminalBridge(columns:UInt32(session.columns),rows:UInt32(session.rows),sequence:1)
        guard core != nil else{throw ClientError.unavailable("无法创建原生终端。")}
        // Local snapshot restore is display-only; the host snapshot below stays authoritative,
        // so the stored ack is never used as the subscribe resume point.
        if let persisted=try? state.load(PersistedTerminalSnapshot.storageName(session.id),as:PersistedTerminalSnapshot.self),persisted.version==1,!persisted.snapshot.isEmpty{
            do{try core?.importSnapshot(persisted.snapshot);localInputEpoch=max(localInputEpoch,persisted.localInputEpoch);if let raw=try core?.renderFrame(){frame=try TerminalFrame.decode(raw)}}
            catch{core=try TLTerminalBridge(columns:UInt32(session.columns),rows:UInt32(session.rows),sequence:1)}
        }
        try relay.bindSession(session.id,pcID:pc.id){[weak self] event in try await self?.receive(event)}
        _=try await api.request("/v1/sessions/\(session.id)/subscribe",method:"POST",body:.object([:]),idempotency:UUID())
        guard owner==incarnation,!closed else{throw CancellationError()}
        var payload=ProtoMessage();payload.set(1,ack);payload.set(2,try identity.publicExchange);payload.set(4,attachID.bytes)
        // Advertise only implemented decoder contracts. Graphics/Unicode16 require their own native frame adapters.
        for c in ["terminal_attach_v1","terminal_state_2_21_v2","terminal_host_geometry_v1","terminal_host_theme_v1","terminal_host_resize_v1"]{payload.add(3,Data(c.utf8))}
        _=try await relay.send(route:session.id,type:.subscribe,payload:payload)
        status="正在确认终端协议"
        deadline=Task{[weak self] in do{try await Task.sleep(for:.seconds(15));guard let self,self.incarnation==owner,!self.attached else{return};self.invalidate("主机未及时确认终端协议，请重新打开会话。")}catch{}}
    }
    func invalidate(_ reason:String){canInput=false;lease=nil;attached=false;frame=nil;bootstrap=[];snapshotID=nil;snapshotBytes=Data();renewal?.cancel();deadline?.cancel();resizeTask?.cancel();status="状态待确认";error=reason;incarnation=UUID()}
    func close(){guard !closed else{return};let held=lease;closed=true;invalidate("会话视图已关闭。");relay.unbindSession(session.id);core=nil
        if let held{let api=api,id=session.id;Task{_=try? await api.request("/v1/sessions/\(id)/lease/\(held)",method:"DELETE",idempotency:UUID())}}
    }
    private func leaseDate(_ value:String)->Date? {
        let formatter=ISO8601DateFormatter();formatter.formatOptions=[.withInternetDateTime,.withFractionalSeconds]
        return formatter.date(from:value) ?? ISO8601DateFormatter().date(from:value)
    }
    func requestControl() async throws {
        guard attached,!closed,!frozen,relay.state == .connected else{throw ClientError.readOnly}
        let owner=incarnation,result=try await api.request("/v1/sessions/\(session.id)/lease",method:"POST",body:.object([:]),idempotency:UUID())
        guard owner==incarnation,let id=UUID(uuidString:result["lease_id"].string),result["version"].integer>0,
              let expires=leaseDate(result["expires_at"].string),expires>Date() else{throw ClientError.readOnly}
        lease=id;leaseVersion=UInt64(result["version"].integer);leaseExpiry=expires;canInput=true;status="控制中";activity?.record("lease-granted","获得了「\(title)」的控制权")
        if let viewport{fit(columns:viewport.0,rows:viewport.1,cellWidth:viewport.2,cellHeight:viewport.3)}
        renewal?.cancel();renewal=Task{[weak self] in
            while !Task.isCancelled {do{try await Task.sleep(for:.seconds(10));guard let self,self.incarnation==owner,self.lease==id else{return}
                let result=try await self.api.request("/v1/sessions/\(self.session.id)/lease/\(id)/renew",method:"POST",body:.object([:]),idempotency:UUID())
                guard self.incarnation==owner,self.lease==id,let expiry=self.leaseDate(result["expires_at"].string),expiry>Date() else{throw ClientError.readOnly};self.leaseExpiry=expiry
            }catch{if self?.incarnation==owner{self?.canInput=false;self?.status="只读"};return}}
        }
    }
    func releaseControl() async throws{guard let id=lease else{return};canInput=false;lease=nil;renewal?.cancel();status="只读";_=try await api.request("/v1/sessions/\(session.id)/lease/\(id)",method:"DELETE",idempotency:UUID());activity?.record("lease-released","释放了「\(title)」的控制权")}
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
        _=try await relay.send(route:session.id,type:.input,payload:p,acknowledgement:ack)
    }
    /// Explicit window resize mirroring Android `requestWindowResize`: measured metrics apply a
    /// phone-sized grid, `restore:true` (ResizePayload.restore = 8, zeroed dimensions) asks the
    /// host to put back its own desktop window size. No blind retries.
    func resize(columns:Int,rows:Int,cellWidth:Int,cellHeight:Int,restore:Bool=false) async throws ->(columns:Int,rows:Int){
        guard canInput,let lease,restore || ((2...1000).contains(columns)&&(2...1000).contains(rows)) else{throw ClientError.readOnly}
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
        guard canInput,attached,pc.capabilities.contains("terminal_window_resize_v1"),frame?.columns != desired.0 || frame?.rows != desired.1 else{return}
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
        if event.type == .leaseRevoked || event.type == .leaseDenied {canInput=false;lease=nil;status="只读";localInputEpoch=max(localInputEpoch,p.uint(5));if event.type == .leaseRevoked{activity?.record("lease-revoked","「\(title)」的控制权已被释放")};return}
        if event.type == .inputRejected {canInput=false;status="只读";error=try p.text(3);return}
        if event.type == .inputAccepted || event.type == .leaseGranted {return}
        guard attached else{return}
        guard [.output,.resizeApplied,.geometry,.hostTheme,.hostResize,.gap,.upsert,.removed,.theme,.started,.ended,.interrupted,.brokerOutput].contains(event.type) else{return}
        if snapshotID != nil {guard bootstrap.count<128 else{throw ClientError.limitExceeded};bootstrap.append(event);return}
        guard event.sequence>ack else{return}
        guard event.sequence==ack+1 else{canInput=false;status="恢复历史中";try await requestSnapshot();return}
        do{
            switch event.type {
            case .output:try core?.applyData(p.bytes(1),kind:1,sequence:event.sequence,columns:0,rows:0)
            case .resizeApplied:
                guard (1...512).contains(p.uint(1)),(1...512).contains(p.uint(2)) else{throw ClientError.protocolViolation("resize dimensions")}
                try core?.applyData(Data(),kind:2,sequence:event.sequence,columns:UInt32(p.uint(1)),rows:UInt32(p.uint(2)))
            case .geometry:try core?.applyData(p.bytes(1),kind:5,sequence:event.sequence,columns:0,rows:0)
            case .hostTheme:try core?.applyData(p.bytes(1),kind:6,sequence:event.sequence,columns:0,rows:0)
            case .gap:canInput=false;try await requestSnapshot();return
            case .brokerOutput:invalidate("当前 iOS 终端未支持 Broker 图形捕获。");return
            case .ended,.interrupted:canInput=false;lease=nil;status="已结束";fallthrough
            case .upsert,.removed,.theme,.started,.hostResize:try core?.applyData(Data(),kind:3,sequence:event.sequence,columns:0,rows:0)
            default:return
            }
            guard let raw=try core?.renderFrame() else{throw ClientError.readOnly};let next=try TerminalFrame.decode(raw)
            guard next.nextSequence==event.sequence+1 else{throw ClientError.protocolViolation("applied sequence")}
            // Never acknowledge bytes that the native core could not validate and apply.
            // Persist the validated native snapshot before advancing the durable event acknowledgement.
            guard let snapshot=try core?.exportSnapshot() else{throw ClientError.readOnly}
            try state.save(PersistedTerminalSnapshot(version:1,ack:event.sequence,localInputEpoch:localInputEpoch,snapshot:snapshot),name:PersistedTerminalSnapshot.storageName(session.id));frame=next;ack=event.sequence;try await acknowledge()
        }catch{invalidate("终端状态校验失败，已停止输入与 ACK。");throw error}
    }
    private func acknowledge() async throws{var p=ProtoMessage();p.set(1,ack);_=try await relay.send(route:session.id,type:.ack,payload:p,acknowledgement:ack)
        if relay.capabilities.contains("flow_control_v1"){var flow=ProtoMessage();flow.set(1,ack);flow.set(2,UInt64(32));flow.set(3,UInt64(3*1024*1024));flow.set(6,UInt64(1));_=try await relay.send(route:session.id,type:.flow,payload:flow,acknowledgement:ack)}
    }
    private func requestSnapshot() async throws {
        let id=UUID();snapshotID=id;snapshotObject=nil;snapshotBytes=Data();snapshotIndex=0;bootstrap=[]
        var p=ProtoMessage();p.set(1,id.bytes);p.set(2,UInt64(1));p.add(8,Data("terminal_state_2_21_v2".utf8));_=try await relay.send(route:session.id,type:.historyRequest,payload:p)
        let owner=incarnation;deadline?.cancel();deadline=Task{[weak self] in do{try await Task.sleep(for:.seconds(90));guard let self,self.incarnation==owner,self.snapshotID==id else{return};self.invalidate("主机历史同步超时，请重新打开会话。")}catch{}}
    }
    private func historyChunk(_ p:ProtoMessage) async throws {
        guard let id=snapshotID,p.bytes(1)==id.bytes else{return}
        guard p.uint(2)==1,p.uint(3)==1,p.uint(21)>0,p.uint(21)<=64*1024*1024+4096,p.uint(22)==snapshotIndex,p.uint(23)>0,p.uint(23)<=129 else{invalidate("主机历史尚不可用。");return}
        let object=try UUID(bytes:p.bytes(4))
        if snapshotObject==nil{snapshotObject=object;snapshotHash=p.bytes(20);snapshotCount=p.uint(23);snapshotSize=p.uint(21)}
        guard snapshotObject==object,p.bytes(20)==snapshotHash,p.uint(23)==snapshotCount,p.uint(21)==snapshotSize,p.bytes(24).count<=512*1024,snapshotBytes.count+p.bytes(24).count<=Int(snapshotSize) else{throw ClientError.protocolViolation("snapshot chunks")}
        snapshotBytes.append(p.bytes(24));snapshotIndex+=1
        var control=ProtoMessage();control.set(1,id.bytes);control.set(2,UInt64(1));control.set(3,object.bytes);control.set(4,p.uint(22));_=try await relay.send(route:session.id,type:.historyControl,payload:control)
        guard snapshotIndex==snapshotCount else{return}
        guard snapshotBytes.count==Int(snapshotSize),Data(SHA256.hash(data:snapshotBytes))==snapshotHash else{throw ClientError.protocolViolation("snapshot hash")}
        // The object is separately recipient-sealed even though every transport chunk was authenticated.
        let owner=incarnation,container=try ProtoMessage(snapshotBytes,maximum:64*1024*1024+4096)
        guard container.uint(1)==2,container.bytes(2)==session.id.bytes,container.bytes(3)==pc.deviceID.bytes,container.bytes(4)==identity.id.bytes,
              container.uint(5)==UInt64(pc.keyEpoch),container.uint(12)==0,container.uint(13)<=64*1024*1024,
              container.uint(7)==p.uint(5),container.uint(7)>0,container.uint(7)<=UInt64(Int64.max),container.uint(7)>=ack,container.uint(8)>0,container.uint(8)<=512,container.uint(9)>0,container.uint(9)<=512,
              try container.text(10)=="TERMLINK_TERMINAL_CORE_SNAPSHOT",container.uint(11)==2,container.bytes(14).count==12 else{throw ClientError.protocolViolation("snapshot identity")}
        let recipient=identity.id.uuidString.replacingOccurrences(of:"-",with:"").lowercased()
        let keyID=String(format:"terminal-core-snapshot-v2:%@:%08lld",recipient,pc.keyEpoch)
        guard try container.text(6)==keyID else{throw ClientError.protocolViolation("snapshot key")}
        guard let peer=try? await api.request("/v1/devices"),let device=try peer["items"].array.map(DeviceRecord.init).first(where:{$0.id==pc.deviceID}) else{throw ClientError.readOnly}
        guard owner==incarnation,!closed,device.trust=="TRUSTED" else{throw CancellationError()}
        let pair=try SessionCrypto.pairwise(identity:identity,peer:device.exchangeKey,pc:pc.deviceID,mobile:identity.id,epoch:pc.keyEpoch)
        let key=SessionCrypto.derive(pair,info:Data("TERMLINK-TERMINAL-CORE-SNAPSHOT-KEY-V2\0".utf8)+session.id.bytes+identity.id.bytes+.bigEndian(UInt64(pc.keyEpoch))+Data(keyID.utf8))
        var aad=Data("TERMLINK-TERMINAL-CORE-SNAPSHOT-OBJECT-V2\0".utf8)
        func u32(_ v:UInt64){var n=UInt32(v).bigEndian;aad.append(withUnsafeBytes(of:&n){Data($0)})}
        func string(_ d:Data){var n=UInt16(d.count).bigEndian;aad.append(withUnsafeBytes(of:&n){Data($0)});aad.append(d)}
        u32(container.uint(1));aad.append(container.bytes(2));aad.append(container.bytes(3));aad.append(container.bytes(4));aad.append(.bigEndian(container.uint(5)));string(container.bytes(6));aad.append(.bigEndian(container.uint(7)));u32(container.uint(8));u32(container.uint(9));string(container.bytes(10));u32(container.uint(11));u32(container.uint(12));aad.append(.bigEndian(container.uint(13)));aad.append(.bigEndian(container.uint(16)));aad.append(.bigEndian(container.uint(17)));aad.append(container.bytes(14))
        let plain=try SessionCrypto.open(container.bytes(15),key:key,nonce:container.bytes(14),aad:aad)
        guard plain.count==Int(container.uint(13)) else{throw ClientError.protocolViolation("snapshot size")}
        try core?.importSnapshot(plain);guard let raw=try core?.renderFrame() else{throw ClientError.readOnly}
        let rendered=try TerminalFrame.decode(raw);guard rendered.nextSequence==container.uint(7)+1 else{throw ClientError.protocolViolation("snapshot sequence")}
        try state.save(PersistedTerminalSnapshot(version:1,ack:container.uint(7),localInputEpoch:container.uint(17),snapshot:plain),name:PersistedTerminalSnapshot.storageName(session.id))
        frame=rendered;ack=container.uint(7);localInputEpoch=container.uint(17);snapshotID=nil;snapshotBytes=Data();deadline?.cancel();try await acknowledge();activity?.record("snapshot-restored","「\(title)」历史缺口已恢复，终端画面已重建")
        let waiting=bootstrap;bootstrap=[];for event in waiting{try await receive(event)}
    }
}
