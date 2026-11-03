// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import Foundation
import Combine

@MainActor final class RemoteFileStore: ObservableObject {
    @Published private(set) var entries:[RemoteEntry]=[]
    @Published private(set) var downloads:[DownloadRecord]=[]
    @Published var pcID:UUID?
    @Published var path=""
    @Published var query=""
    @Published var busy=false
    @Published var error:String?
    @Published var nextOffset:Int64?
    @Published var parentPath:String?
    private var jobs:[UUID:Task<Void,Never>]=[:]
    private var favorites:[String:[String]]=[:]
    private unowned let product:ProductStore
    private var epoch=UUID()
    init(product:ProductStore){self.product=product}
    func restore(){do{downloads=try product.privateState.load("downloads",as:[DownloadRecord].self) ?? [];favorites=try product.privateState.load("directory-favorites",as:[String:[String]].self) ?? [:];for i in downloads.indices where !["COMPLETED","CANCELLED"].contains(downloads[i].status){downloads[i].status="RESUMING"}}catch{product.report(error)}}
    private func save(){do{try product.privateState.save(downloads,name:"downloads");try product.privateState.save(favorites,name:"directory-favorites")}catch{product.report(error)}}
    func reset(){epoch=UUID();jobs.values.forEach{$0.cancel()};jobs=[:];entries=[];downloads=[];pcID=nil;path="";query="";busy=false;error=nil;favorites=[:]}
    func pause(){jobs.values.forEach{$0.cancel()}}
    var currentFavorites:[String]{guard let pcID else{return []};return favorites[pcID.uuidString] ?? []}
    func toggleFavorite(){guard let pcID,!path.isEmpty else{return};var a=currentFavorites;if a.contains(path){a.removeAll{$0==path}}else{a.insert(path,at:0)};favorites[pcID.uuidString]=Array(a.prefix(128));save()}
    func list(pc:UUID,path:String,offset:Int64=0) async {
        guard !busy else{return};let owner=epoch;busy=true;defer{if owner==epoch{busy=false}};error=nil
        do{
            let value=try await operation(pc:pc,kind:"LIST",path:path,extra:["offset":.number(Double(offset)),"query":.string(query)])
            guard owner==epoch else{return}
            let result=value["result"];guard result["entries"].array.count<=4096,result["current_path"].string.utf8.count<=4096,result["next_offset"] == .null || result["next_offset"].integer>offset else{throw ClientError.limitExceeded}
            for entry in result["entries"].array{guard ["file","directory"].contains(entry["kind"].string),!entry["path"].string.isEmpty,entry["path"].string.utf8.count<=4096,!entry["path"].string.contains("\0") else{throw ClientError.invalidResponse("remote file entry")}}
            pcID=pc;self.path=result["current_path"].string;parentPath=result["parent_path"] == .null ? nil:result["parent_path"].string
            let next=result["entries"].array.map(RemoteEntry.init);var ids=Set<String>();entries=(offset==0 ? next:entries+next).filter{ids.insert($0.id).inserted}
            nextOffset=result["next_offset"] == .null ? nil:result["next_offset"].integer
        }catch{if owner==epoch{self.error=error.localizedDescription}}
    }
    func operation(pc:UUID,kind:String,path:String,extra:[String:JSONValue]=[:]) async throws -> JSONValue {
        guard let api=product.api,path.utf8.count<=4096,!path.contains("\0"),query.utf8.count<=512 else{throw ClientError.limitExceeded}
        let owner=epoch
        var body=extra;body["operation"] = .string(kind);body["path"] = .string(path)
        let created=try await api.request("/v1/pcs/\(pc)/file-operations",method:"POST",body:.object(body),idempotency:UUID())
        guard owner==epoch else{throw CancellationError()}
        guard let id=UUID(uuidString:created["file_operation"]["id"].string) else{throw ClientError.invalidResponse("file operation ID")}
        return try await wait(pc:pc,id:id,initial:created)
    }
    func wait(pc:UUID,id:UUID,initial:JSONValue) async throws -> JSONValue {
        guard let api=product.api else{throw ClientError.authentication};let owner=epoch;var result=initial
        for _ in 0..<120 {
            try Task.checkCancellation();guard owner==epoch else{throw CancellationError()};let task=result["file_operation"]
            guard task["id"].string.lowercased()==id.uuidString.lowercased() else{throw ClientError.protocolViolation("file operation changed")}
            switch task["status"].string{
            case "SUCCEEDED":return task
            case "FAILED","EXPIRED","CANCELLED":throw ClientError.unavailable(task["error"].string.isEmpty ? "电脑文件操作未完成。":task["error"].string)
            case "PENDING","PROCESSING":break
            default:throw ClientError.invalidResponse("file operation status")
            }
            try await Task.sleep(for:.milliseconds(500));result=try await api.request("/v1/pcs/\(pc)/file-operations/\(id)")
        }
        throw ClientError.unavailable("文件操作结果待确认。")
    }
    func openShell(pc:UUID,path:String,command:String) async throws {
        let owner=epoch,result=try await operation(pc:pc,kind:"OPEN_TERMINAL",path:path,extra:["terminal_command_id":.string(command)])
        guard owner==epoch,let id=UUID(uuidString:result["result"]["session_id"].string) else{throw ClientError.invalidResponse("created session")}
        await product.refreshCatalogs();guard owner==epoch else{throw CancellationError()};if let s=product.sessions.first(where:{$0.pcID==pc && $0.id==id}){await product.open(s)}
    }
    func download(_ entry:RemoteEntry,pc:PC) async throws {
        guard let api=product.api,let identity=product.identity,let device=product.devices.first(where:{$0.id==pc.deviceID}) else{throw ClientError.authentication}
        let owner=epoch
        let created=try await api.request("/v1/pcs/\(pc.id)/file-operations",method:"POST",body:.object(["operation":.string("DOWNLOAD"),"path":.string(entry.id)]),idempotency:UUID())
        guard owner==epoch else{throw CancellationError()}
        guard let id=UUID(uuidString:created["file_operation"]["id"].string) else{throw ClientError.invalidResponse("download ID")}
        let name=entry.name.replacingOccurrences(of:"/",with:"_").replacingOccurrences(of:"\\",with:"_")+(entry.directory ? ".zip":"")
        let record=DownloadRecord(id:UUID(),pcID:pc.id,operationID:id,path:entry.id,filename:name,directoryZIP:entry.directory,expectedBytes:0,received:0,status:"PREPARING",digest:Data())
        downloads.append(record);save();start(record,pc:pc,device:device,api:api,identity:identity)
    }
    func retry(_ id:UUID){guard let record=downloads.first(where:{$0.id==id}),let pc=product.pcs.first(where:{$0.id==record.pcID}),let device=product.devices.first(where:{$0.id==pc.deviceID}),let api=product.api,let identity=product.identity else{return};start(record,pc:pc,device:device,api:api,identity:identity)}
    private func start(_ record:DownloadRecord,pc:PC,device:DeviceRecord,api:APIClient,identity:DeviceIdentity){guard jobs[record.id]==nil else{return};let owner=epoch;jobs[record.id]=Task{[weak self] in guard let self else{return};defer{if self.epoch==owner{self.jobs.removeValue(forKey:record.id)}};do{
        let receiver=FileTransferReceiver(api:api,identity:identity,pc:pc,device:device,record:record)
        try await receiver.receive{value in if self.epoch==owner{self.update(value)}}
        guard self.epoch==owner else{throw CancellationError()}
        let latest=try await api.request("/v1/pcs/\(pc.id)/file-operations/\(record.operationID)")
        _=try await self.wait(pc:pc.id,id:record.operationID,initial:latest)
        guard self.epoch==owner,var done=self.downloads.first(where:{$0.id==record.id}) else{throw CancellationError()}
        done.status="COMPLETED";done.error=nil;self.update(done)
    }catch is CancellationError{if self.epoch==owner{self.setError(record.id,"已暂停，可重试。",status:"RESUMING")}}catch{if self.epoch==owner{self.setError(record.id,error.localizedDescription,status:"FAILED")}}}}
    private func update(_ value:DownloadRecord){if let i=downloads.firstIndex(where:{$0.id==value.id}){downloads[i]=value;save()}}
    private func setError(_ id:UUID,_ text:String,status:String){if let i=downloads.firstIndex(where:{$0.id==id}){downloads[i].error=text;downloads[i].status=status;save()}}
    func cancel(_ id:UUID){jobs[id]?.cancel();guard let r=downloads.first(where:{$0.id==id}),let api=product.api else{return};let owner=epoch;Task{do{_=try await api.request("/v1/pcs/\(r.pcID)/file-operations/\(r.operationID)",method:"DELETE",idempotency:UUID());guard owner==epoch else{return};setError(id,"已取消",status:"CANCELLED")}catch{if owner==epoch{product.report(error)}}}}
    func removeRecord(_ id:UUID){guard jobs[id]==nil,let record=downloads.first(where:{$0.id==id}) else{return}
        do{let url=try FileTransferReceiver.cacheURL(record);if FileManager.default.fileExists(atPath:url.path){try FileManager.default.removeItem(at:url)};downloads.removeAll{$0.id==id};save()}catch{product.report(error)}
    }
    func localFile(_ r:DownloadRecord) throws -> URL {try FileTransferReceiver.cacheURL(r)}
}

@MainActor final class FileTransferReceiver {
    let api:APIClient,identity:DeviceIdentity,pc:PC,device:DeviceRecord
    var record:DownloadRecord
    init(api:APIClient,identity:DeviceIdentity,pc:PC,device:DeviceRecord,record:DownloadRecord){self.api=api;self.identity=identity;self.pc=pc;self.device=device;self.record=record}
    static func cacheURL(_ r:DownloadRecord) throws -> URL {
        let root=try FileManager.default.url(for:.applicationSupportDirectory,in:.userDomainMask,appropriateFor:nil,create:true).appendingPathComponent("Downloads",isDirectory:true)
        try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
        var location=root;var values=URLResourceValues();values.isExcludedFromBackup=true;try location.setResourceValues(values)
        let directory=root.appendingPathComponent(r.id.uuidString,isDirectory:true);try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
        let filename=r.filename;guard !filename.isEmpty,filename != ".",filename != "..",!filename.contains("/"),!filename.contains("\\"),!filename.contains("\0") else{throw ClientError.invalidResponse("download filename")}
        return directory.appendingPathComponent(filename)
    }
    private func receive(_ socket:URLSessionWebSocketTask) async throws->URLSessionWebSocketTask.Message {
        try await withTaskCancellationHandler(operation:{
            try await withThrowingTaskGroup(of:URLSessionWebSocketTask.Message.self){group in
                group.addTask{try await socket.receive()}
                group.addTask{try await Task.sleep(for:.seconds(30));socket.cancel(with:.goingAway,reason:nil);throw ClientError.unavailable("文件传输超时，可重新查询并续传。")}
                defer{group.cancelAll()};return try await group.next()!
            }
        },onCancel:{socket.cancel(with:.goingAway,reason:nil)})
    }
    func receive(progress:(DownloadRecord)->Void) async throws {
        let id=record.operationID,keyID=Data("file-transfer-v2:\(id.uuidString.replacingOccurrences(of:"-",with:"").lowercased()):\(pc.keyEpoch)".utf8)
        let pair=try SessionCrypto.pairwise(identity:identity,peer:device.exchangeKey,pc:device.id,mobile:identity.id,epoch:pc.keyEpoch)
        func transferKey(_ direction:UInt8)->SymmetricKey{SessionCrypto.derive(pair,info:Data("TERMLINK-FILE-TRANSFER-KEY-V2\0".utf8)+id.bytes+Data([direction]) + Data.bigEndian(UInt64(pc.keyEpoch))+keyID)}
        let signed=try await api.signedRequest(path:"/v1/file-transfers",method:"GET")
        let request=try MobileRelayProtocol.webSocketRequest(signed,channel:.files)
        let delegate=NoRedirectDelegate(),session=URLSession(configuration:.ephemeral,delegate:delegate,delegateQueue:nil)
        let socket=session.webSocketTask(with:request);socket.maximumMessageSize=1_048_575;socket.resume();defer{socket.cancel(with:.goingAway,reason:nil);session.invalidateAndCancel()}
        let hello=JSONValue.object(["role":.string("android"),"transfer_id":.string(id.uuidString.lowercased()),"protocol_versions":.array([.number(2)]),"capabilities":.array([.string("e2ee"),.string("resume_offset_v2")])])
        try await socket.send(.string(String(decoding:try hello.data(),as:UTF8.self)))
        guard case .string(let welcome)=try await receive(socket),try JSONValue.decode(Data(welcome.utf8))["protocol_version"].integer==2 else{throw ClientError.protocolViolation("file welcome")}
        let path=try Self.cacheURL(record);if !FileManager.default.fileExists(atPath:path.path){FileManager.default.createFile(atPath:path.path,contents:nil,attributes:[.protectionKey:FileProtectionType.complete])}
        let file=try FileHandle(forUpdating:path);defer{try? file.close()}
        var committed=try file.seekToEnd(),incoming:UInt64=0,outgoing:UInt64=UInt64(Date().timeIntervalSince1970*1000)*1024
        var offered=false
        func send(_ type:UInt64,_ body:ProtoMessage) async throws {
            outgoing+=1;var payload=ProtoMessage();payload.set(Int(type),body.encoded());var m=ProtoMessage();m.set(1,UInt64(2));m.set(2,id.bytes);m.set(3,identity.id.bytes);m.set(4,pc.deviceID.bytes);m.set(5,type);m.set(6,outgoing);m.set(7,UInt64(pc.keyEpoch));m.set(8,keyID);m.set(9,try SessionCrypto.random(12));m.set(10,try SessionCrypto.seal(payload.encoded(),key:transferKey(2),nonce:m.bytes(9),aad:m.encoded()));try await socket.send(.data(m.encoded()))
        }
        while !Task.isCancelled {
            guard case .data(let data)=try await receive(socket) else{throw ClientError.protocolViolation("file frame")}
            let m=try ProtoMessage(data);try m.validateSingular(Set(1...10))
            guard m.uint(1)==2,m.bytes(2)==id.bytes,m.bytes(3)==pc.deviceID.bytes,m.bytes(4)==identity.id.bytes,m.uint(6)>incoming,m.uint(7)==UInt64(pc.keyEpoch),m.bytes(8)==keyID else{throw ClientError.protocolViolation("file route")};incoming=m.uint(6)
            let payload=try ProtoMessage(SessionCrypto.open(m.bytes(10),key:transferKey(1),nonce:m.bytes(9),aad:m.encoded(excluding:[10])))
            let type=m.uint(5);guard (1...8).contains(type) else{throw ClientError.protocolViolation("file type")};let body=try ProtoMessage(payload.bytes(Int(type)))
            switch type {
            case 1:
                let name=try body.text(1);guard !name.isEmpty,!name.contains("/"),!name.contains("\\"),body.uint(2)<=5<<30,body.bytes(3).count==32,body.uint(5)==256*1024,body.uint(4)==(record.directoryZIP ? 1:0),committed<=body.uint(2) else{throw ClientError.protocolViolation("file offer")}
                if !record.digest.isEmpty{guard record.digest==body.bytes(3),record.expectedBytes==body.uint(2) else{throw ClientError.protocolViolation("resume content changed")}}
                record.expectedBytes=body.uint(2);record.digest=body.bytes(3);record.received=committed;record.status="DOWNLOADING";progress(record);offered=true
                var response=ProtoMessage();response.set(1,committed);try await send(2,response)
            case 3:
                let chunk=body.bytes(2);guard offered,body.uint(1)==committed,!chunk.isEmpty,chunk.count<=256*1024,UInt64(chunk.count)<=record.expectedBytes-committed else{throw ClientError.protocolViolation("file chunk")}
                try file.write(contentsOf:chunk);try file.synchronize();committed+=UInt64(chunk.count);record.received=committed;progress(record)
                var ack=ProtoMessage();ack.set(1,committed);try await send(4,ack)
            case 5:
                guard offered,committed==record.expectedBytes,body.uint(1)==committed,body.bytes(2)==record.digest else{throw ClientError.protocolViolation("file completion")}
                record.status="VERIFYING";progress(record)
                guard try await FileDigest.sha256(path,size:committed,limit:5<<30)==record.digest else{throw ClientError.protocolViolation("file SHA-256")}
                var ack=ProtoMessage();ack.set(1,committed);ack.set(2,record.digest);try await send(6,ack);return
            case 7,8:throw ClientError.unavailable("电脑停止了文件传输。")
            default:throw ClientError.protocolViolation("file message type")
            }
        }
        throw CancellationError()
    }
}
