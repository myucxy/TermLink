// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import Foundation
import Combine

@MainActor final class RelayClient: ObservableObject {
    enum State: Equatable { case disconnected, connecting, connected, failed(String) }
    struct Peer { let pcID: UUID, deviceID: UUID, key: Data, epoch: Int64 }
    struct Opened { let route: UUID, type: WireType, sequence: UInt64, payload: ProtoMessage, generation: UUID }
    @Published private(set) var state: State = .disconnected
    private(set) var capabilities = Set<String>()
    private var peers: [UUID:Peer] = [:]
    private var sequences: [UUID:UInt64] = [:]
    private var handlers: [UUID:(Opened) async throws -> Void] = [:]
    private struct Pending {
        let route: UUID, response: WireType, sequence: UInt64, generation: UUID
        let continuation: CheckedContinuation<ProtoMessage, Error>
        let timeout: Task<Void,Never>
    }
    private var pending: [UUID:Pending] = [:]
    private var socket: URLSessionWebSocketTask?
    private var receiveTask: Task<Void,Never>?, pingTask: Task<Void,Never>?
    private var generation = UUID()
    private var probe:String?,probeSent=Date.distantPast
    private let api: APIClient, identity: DeviceIdentity
    private let delegate = NoRedirectDelegate()
    private lazy var session = URLSession(configuration: .ephemeral, delegate: delegate, delegateQueue: nil)
    init(api: APIClient, identity: DeviceIdentity) { self.api = api; self.identity = identity }
    func bind(pc: PC, device: DeviceRecord) throws {
        guard pc.deviceID == device.id, device.trust == "TRUSTED", pc.keyEpoch > 0, device.exchangeKey.count == 32 else { throw ClientError.readOnly }
        if let old = peers[pc.id], old.deviceID != device.id || old.key != device.exchangeKey || old.epoch != pc.keyEpoch {
            let obsolete=peers.filter{$0.value.pcID==pc.id}.map(\.key)
            for id in obsolete{handlers.removeValue(forKey:id);peers.removeValue(forKey:id)}
            disconnect()
        }
        peers[pc.id] = .init(pcID:pc.id,deviceID:device.id,key:device.exchangeKey,epoch:pc.keyEpoch)
    }
    func bindSession(_ id: UUID, pcID: UUID, handler: @escaping (Opened) async throws -> Void) throws {
        guard let peer = peers[pcID] else { throw ClientError.readOnly }; peers[id] = peer; handlers[id] = handler
    }
    func unbindSession(_ id: UUID) { handlers.removeValue(forKey:id); peers.removeValue(forKey:id) }
    func retainPCs(_ allowed:Set<UUID>) {
        let removed=Set(peers.filter{!allowed.contains($0.value.pcID)}.map(\.key))
        for id in removed{peers.removeValue(forKey:id);handlers.removeValue(forKey:id)}
        for id in pending.filter({removed.contains($0.value.route)}).map(\.key){complete(id,.failure(ClientError.readOnly))}
    }
    func connect() async throws {
        if state == .connected || state == .connecting { return }
        disconnect(); state = .connecting; let owner = generation
        defer{if owner==generation,state != .connected{disconnect();state = .failed("Relay 握手未完成，请重新连接。")}}
        let signed = try await api.signedRequest(path:"/v1/realtime",method:"GET")
        let request = try MobileRelayProtocol.webSocketRequest(signed, channel: .realtime)
        let task = session.webSocketTask(with:request); task.maximumMessageSize = 1_048_575; socket = task; task.resume()
        // 'android' is the existing mobile endpoint wire role, also used by IOS identities.
        let supported = ["cumulative_ack","e2ee_x25519_chacha20poly1305","flow_control_v1","lease_v1","liveness_probe_v1","bounded_event_replay_v1","terminal_presentation_metadata_v1","terminal_typed_input_v1","terminal_attach_v1","terminal_host_geometry_v1","terminal_host_theme_v1","terminal_host_resize_v1","host_history_v1","host_session_catalog_v1","host_session_cleanup_v1","ai_agent_v1"]
        let hello = JSONValue.object(["role":.string("android"),"protocol_versions":.array([.number(1)]),"capabilities":.array(supported.sorted().map(JSONValue.string))])
        try await task.send(.string(String(decoding:try hello.data(),as:UTF8.self)))
        let first = try await withThrowingTaskGroup(of: URLSessionWebSocketTask.Message.self) { group in
            group.addTask { try await task.receive() }
            group.addTask { try await Task.sleep(for:.seconds(15)); task.cancel(with:.goingAway,reason:nil); throw ClientError.unavailable("Relay 握手超时。") }
            defer { group.cancelAll() }; return try await group.next()!
        }
        guard owner == generation, case .string(let text) = first else { throw ClientError.protocolViolation("welcome") }
        let welcome = try JSONValue.decode(Data(text.utf8),maximum:65536)
        guard welcome["protocol_version"].integer == 1 else { throw ClientError.protocolViolation("Relay version") }
        capabilities = Set(welcome["capabilities"].array.map(\.string)).intersection(supported); state = .connected
        receiveTask = Task { [weak self] in
            do { while !Task.isCancelled { let message = try await task.receive(); guard let self, self.generation == owner else { return }; try await self.receive(message,owner:owner) } }
            catch { guard let self, self.generation == owner else { return }; self.fail(ClientError.unavailable("Relay 连接中断，请重新确认。")) }
        }
        pingTask = Task { [weak self] in
            while !Task.isCancelled {
                do {
                    try await Task.sleep(for:.seconds(15));guard let self,self.generation == owner else { return }
                    if self.probe != nil{guard Date().timeIntervalSince(self.probeSent)<30 else{throw ClientError.unavailable("Relay 未回应状态确认。")}}
                    else{let id=UUID().uuidString.lowercased();self.probe=id;self.probeSent=Date();try await task.send(.string("{\"type\":\"ping\",\"probe_id\":\"\(id)\"}"))}
                    try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                        task.sendPing { error in
                            if let error { continuation.resume(throwing: error) }
                            else { continuation.resume() }
                        }
                    }
                }
                catch { if self?.generation == owner { self?.fail(ClientError.unavailable("Relay 心跳中断。")) }; return }
            }
        }
    }
    func disconnect() {
        generation = UUID(); receiveTask?.cancel(); pingTask?.cancel(); socket?.cancel(with:.goingAway,reason:nil); socket = nil
        capabilities = []; state = .disconnected;probe=nil
        let values = pending; pending.removeAll(); for item in values.values { item.timeout.cancel(); item.continuation.resume(throwing:CancellationError()) }
    }
    private func fail(_ error: Error) { disconnect(); state = .failed(error.localizedDescription) }
    private func next(_ route: UUID) throws -> UInt64 {
        let current = sequences[route] ?? UInt64(Date().timeIntervalSince1970 * 1000)*1024
        guard current < UInt64(Int64.max)-1 else { throw ClientError.limitExceeded }; sequences[route] = current+1; return current+1
    }
    func send(route: UUID, type: WireType, payload: ProtoMessage, acknowledgement: UInt64 = 0, sequence: UInt64? = nil) async throws -> UInt64 {
        guard state == .connected, let socket, let peer = peers[route] else { throw ClientError.unavailable("设备连接尚未确认。") }
        let seq = try sequence ?? next(route), keyID = Data(String(format:"epoch-%08lld",peer.epoch).utf8)
        let pair = try SessionCrypto.pairwise(identity:identity,peer:peer.key,pc:peer.deviceID,mobile:identity.id,epoch:peer.epoch)
        let key = SessionCrypto.session(pair,id:route,direction:2,keyID:keyID)
        let envelope = try SessionCrypto.envelope(id:route,sender:identity.id,recipient:peer.deviceID,type:type,sequence:seq,ack:acknowledgement,keyID:keyID,key:key,payload:payload.encoded())
        let data = envelope.encoded(); guard data.count < 1_048_576 else { throw ClientError.limitExceeded }
        try await socket.send(.data(data)); return seq
    }
    func request(route: UUID, type: WireType, response: WireType, capability: String, timeout: Duration = .seconds(40), body: (UUID) throws -> ProtoMessage) async throws -> ProtoMessage {
        guard capabilities.contains(capability), pending.count < 16 else { throw ClientError.unavailable("电脑或服务器缺少 \(capability)。") }
        let id = UUID(), seq = try next(route), payload = try body(id), owner = generation
        return try await withTaskCancellationHandler(operation: {
            try await withCheckedThrowingContinuation { continuation in
                let timer = Task { [weak self] in do { try await Task.sleep(for:timeout); self?.complete(id,.failure(ClientError.unavailable("请求结果待确认，请重新查询。"))) } catch {} }
                pending[id] = .init(route:route,response:response,sequence:seq,generation:owner,continuation:continuation,timeout:timer)
                Task { do { _ = try await self.send(route:route,type:type,payload:payload,sequence:seq) } catch { self.complete(id,.failure(error)) } }
            }
        }, onCancel: { Task { @MainActor in self.complete(id,.failure(CancellationError())) } })
    }
    private func complete(_ id: UUID, _ result: Result<ProtoMessage,Error>) { guard let item = pending.removeValue(forKey:id) else { return }; item.timeout.cancel(); item.continuation.resume(with:result) }
    private func receive(_ message: URLSessionWebSocketTask.Message, owner: UUID) async throws {
        if case .string(let text)=message{
            let value=try JSONValue.decode(Data(text.utf8),maximum:65536)
            if value.object.count==2,value["type"].string=="pong",value["probe_id"].string==probe{probe=nil};return
        }
        guard case .data(let data) = message else { return }
        let m = try ProtoMessage(data); try m.validateSingular(Set(1...12))
        let route = try UUID(bytes:m.bytes(3))
        guard let peer = peers[route] else { return }
        guard m.uint(1)==1, m.uint(5)==1, m.uint(6)>0, m.uint(6)<=UInt64(Int64.max), m.bytes(4)==peer.deviceID.bytes, m.bytes(12).isEmpty || m.bytes(12)==identity.id.bytes,
              m.bytes(9)==Data(String(format:"epoch-%08lld",peer.epoch).utf8), m.bytes(10).count==12,
              let type=WireType(rawValue:m.uint(2)) else { throw ClientError.protocolViolation("encrypted route") }
        let pair = try SessionCrypto.pairwise(identity:identity,peer:peer.key,pc:peer.deviceID,mobile:identity.id,epoch:peer.epoch)
        let key = SessionCrypto.session(pair,id:route,direction:1,keyID:m.bytes(9))
        let plain = try SessionCrypto.open(m.bytes(11),key:key,nonce:m.bytes(10),aad:m.encoded(excluding:[11]))
        let payload = try ProtoMessage(plain)
        if [.aiResponse,.catalogResponse,.cleanupResponse,.terminateResponse,.resizeResponse].contains(type), let id = try? UUID(bytes:payload.bytes(1)), let item=pending[id] {
            guard item.route==route,item.response==type,item.sequence==m.uint(6),item.generation==owner else { return }
            complete(id,.success(payload)); return
        }
        try await handlers[route]?(.init(route:route,type:type,sequence:m.uint(6),payload:payload,generation:owner))
    }
    func ai(pc: UUID, body: JSONValue) async throws -> JSONValue {
        let raw = try body.data(); guard raw.count <= 48*1024 else { throw ClientError.limitExceeded }
        let response = try await request(route:pc,type:.aiRequest,response:.aiResponse,capability:"ai_agent_v1",timeout:["import-native","resume"].contains(body["action"].string) ? .seconds(120):.seconds(40)) { id in
            var p=ProtoMessage();p.set(1,id.bytes);p.set(2,raw);return p
        }
        return try .decode(response.bytes(2),maximum:900*1024)
    }
    func catalog(pc: UUID) async throws -> [TerminalSession] {
        let response=try await request(route:pc,type:.catalogRequest,response:.catalogResponse,capability:"host_session_catalog_v1",timeout:.seconds(8)) { id in var p=ProtoMessage();p.set(1,id.bytes);return p }
        return try TerminalSession.catalog(response,pc:pc)
    }
}
