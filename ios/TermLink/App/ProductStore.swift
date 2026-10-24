// SPDX-License-Identifier: GPL-3.0-only
import Combine
import Foundation
import Network
import UIKit

@MainActor final class ProductStore: ObservableObject {
    @Published var tab: ProductTab = .devices
    @Published private(set) var pcs: [PC] = [], devices: [DeviceRecord] = [], sessions: [TerminalSession] = []
    @Published private(set) var signedIn = false, refreshing = false
    @Published private(set) var signingIn = false
    private(set) var active=true
    @Published private(set) var catalogErrors: [UUID:String] = [:]
    @Published var error: String?, selectedCLI: UUID?
    @Published var server = UserDefaults.standard.string(forKey:"server") ?? "https://termlink.kget.cc:8910"
    @Published private(set) var relay: RelayClient?
    @Published private(set) var terminals: [UUID:TerminalController] = [:]
    @Published private(set) var accountID = ""
    let authentication = AuthenticationCoordinator()
    let notifications = StatusNotifications()
    private(set) var api: APIClient?, identity: DeviceIdentity?, tokens: TokenStore?
    private var loop: Task<Void,Never>?, epoch=UUID(), restoring=false, reconnecting=false
    private let network=NWPathMonitor(), networkQueue=DispatchQueue(label:"dev.termlink.network")
    lazy var ai = AIWorkspace(product:self)
    lazy var updates = AgentUpdateStore(product:self)
    lazy var files = RemoteFileStore(product:self)
    private let activityStore = ActivityStore()
    var activity: ActivityStore { activityStore }
    var privateState: SecureState { .init(scope:server+"\n"+accountID) }
    init() {
        activityStore.product = self
        notifications.onOpen = { [weak self] pc, id in
            guard let self, self.signedIn, let session=self.ai.conversations[id], session.pcID==pc else { return }
            self.ai.open(session)
        }
        network.pathUpdateHandler = { [weak self] path in Task { @MainActor in
            guard let self,self.signedIn,self.active,path.status == .satisfied else { return }; await self.reconnect()
        } }
        network.start(queue:networkQueue)
    }
    func report(_ error: Error) { if !(error is CancellationError) { self.error = error.localizedDescription } }
    func signIn() async {
        guard !signingIn else { return }; signingIn=true;let owner=epoch;defer{signingIn=false}
        do {
            let root=try APIClient.origin(server); server=root.absoluteString
            let result=try await authentication.signIn(root:root)
            guard owner==epoch else { throw CancellationError() }
            let store=try TokenStore(scope:server);try await store.save(result);try await start(root:root,tokens:store)
            UserDefaults.standard.set(server,forKey:"server")
        } catch { report(error) }
    }
    func restore() async {
        guard !restoring,!signedIn,!signingIn else{return};restoring=true;defer{restoring=false}
        do { let root=try APIClient.origin(server),store=try TokenStore(scope:root.absoluteString);if await store.isSignedIn(){try await start(root:root,tokens:store)} } catch { report(error) }
    }
    private func start(root: URL,tokens: TokenStore) async throws {
        stopRuntime();let owner=epoch
        let localScope = try await tokens.identityScope()
        let identity=try DeviceIdentity.load(scope:root.absoluteString+"\n"+localScope)
        let api=APIClient(root:root,identity:identity,tokens:tokens)
        do { _=try await api.register(name:UIDevice.current.name.prefix(128).description) }
        catch let e as APIError where e.status == 409 {
            let value=try await api.request("/v1/devices/self")
            guard value["device"]["id"].string.lowercased()==identity.id.uuidString.lowercased() else { throw ClientError.authentication }
        }
        let selfValue=try await api.request("/v1/devices/self")
        guard selfValue["device"]["trust_state"].string != "REVOKED" else { throw ClientError.authentication }
        if selfValue["device"]["trust_state"].string == "PENDING" {
            _=try await api.request("/v1/devices/self/trust",method:"POST",body:.object([:]),idempotency:UUID())
        }
        guard owner==epoch else { throw CancellationError() }
        let principal=try await api.request("/v1/devices/self")
        guard principal["device"]["id"].string.lowercased() == identity.id.uuidString.lowercased(), principal["device"]["trust_state"].string == "TRUSTED" else { throw ClientError.authentication }
        let account = localScope
        self.api=api;self.identity=identity;self.tokens=tokens;self.accountID=account;self.signedIn=true
        self.relay=RelayClient(api:api,identity:identity)
        ai.restore();updates.restore();files.restore()
        try await refresh();try await relay?.connect();await refreshCatalogs()
        beginPolling()
        activityStore.record("signed-in","已登录并连接 \(root.host ?? root.absoluteString)")
    }
    func refresh() async throws {
        guard let api,!refreshing else { return };let owner=epoch;refreshing=true;defer{if owner==epoch{refreshing=false}}
        async let rawPCs=api.request("/v1/pcs");async let rawDevices=api.request("/v1/devices")
        let (pcValue,deviceValue)=try await (rawPCs,rawDevices)
        let nextPCs=try pcValue["items"].array.map(PC.init), nextDevices=try deviceValue["items"].array.map(DeviceRecord.init)
        guard owner==epoch else { throw CancellationError() }
        let allowed=Set(nextPCs.filter { pc in pc.online && pc.routing && nextDevices.contains{$0.id==pc.deviceID && $0.trust=="TRUSTED"} }.map(\.id))
        for terminal in terminals.values where !allowed.contains(terminal.session.pcID) { terminal.invalidate("电脑离线或远程访问已关闭。") }
        relay?.retainPCs(allowed)
        for pc in nextPCs where !allowed.contains(pc.id){ai.markUnavailable(pc.id)}
        sessions.removeAll{!allowed.contains($0.pcID)}
        pcs=nextPCs;devices=nextDevices
        for pc in pcs where pc.online && pc.routing {
            guard let d=devices.first(where:{$0.id==pc.deviceID && $0.trust=="TRUSTED"}) else { continue }
            try relay?.bind(pc:pc,device:d)
        }
    }
    func refreshCatalogs() async {
        guard let relay,relay.state == .connected else { return };let owner=epoch
        for pc in pcs where pc.online && pc.routing && pc.capabilities.contains("host_session_catalog_v1") {
            do {
                let list=try await relay.catalog(pc:pc.id);guard owner==epoch else{return}
                sessions.removeAll{$0.pcID==pc.id};sessions+=list;catalogErrors.removeValue(forKey:pc.id)
            } catch is CancellationError { return }
            catch { catalogErrors[pc.id]="状态待确认" }
        }
    }
    func reconnect() async {
        guard signedIn,active,!reconnecting else{return};let owner=epoch;reconnecting=true;defer{if owner==epoch{reconnecting=false}}
        let needsRestore=relay?.state != .connected
        if needsRestore{for t in terminals.values{t.invalidate("正在重新确认会话。")}}
        do {
            try await refresh();try await relay?.connect();await refreshCatalogs();guard epoch==owner else{return}
            if needsRestore{let opened=Array(terminals.keys);for id in opened{if let s=sessions.first(where:{$0.id==id}),catalogErrors[s.pcID]==nil{await open(s)}}}
            await ai.refreshAll()
        }catch{report(error)}
    }
    private func beginPolling() {
        loop?.cancel();loop=Task { [weak self] in
            while !Task.isCancelled {
                do {try await Task.sleep(for:.seconds(6));guard let self else{return};if self.relay?.state != .connected{await self.reconnect()}else{try await self.refresh();await self.refreshCatalogs();await self.ai.refreshAll()};await self.updates.recover()}
                catch is CancellationError{return}catch{self?.report(error)}
            }
        }
    }
    func background() { active=false;loop?.cancel();loop=nil;relay?.disconnect();for t in terminals.values{t.invalidate("已进入后台，返回后重新确认。")};ai.invalidate();files.pause();updates.pause() }
    func foreground() async { active=true;if signedIn{await reconnect();beginPolling()} }
    func signOut() async { authentication.cancel();let store=tokens;notifications.reset();activityStore.record("signed-out","已退出登录并断开本机连接");stopRuntime();do{try await store?.clear()}catch{report(error)} }
    private func stopRuntime(){epoch=UUID();loop?.cancel();loop=nil;relay?.disconnect();relay=nil;terminals.values.forEach{$0.close()};terminals=[:];api=nil;identity=nil;tokens=nil;accountID="";signedIn=false;refreshing=false;reconnecting=false;pcs=[];devices=[];sessions=[];catalogErrors=[:];ai.reset();updates.reset();files.reset()}
    func open(_ session:TerminalSession) async {
        guard let relay,let api,let identity,let pc=pcs.first(where:{$0.id==session.pcID && $0.online && $0.routing}),catalogErrors[pc.id]==nil else{error="先重新确认电脑会话状态。";return}
        do {
            let controller=TerminalController(session:session,pc:pc,relay:relay,api:api,identity:identity,state:privateState)
            controller.activity=activityStore
            terminals[session.id]?.close();terminals[session.id]=controller;selectedCLI=session.id;tab = .cli
            try await controller.open()
        }catch{report(error)}
    }
    func closeView(_ id:UUID){terminals.removeValue(forKey:id)?.close();if selectedCLI==id{selectedCLI=terminals.keys.first}}
    func terminate(_ session:TerminalSession) async throws {
        guard let relay,let pc=pcs.first(where:{$0.id==session.pcID && $0.online && $0.routing}),catalogErrors[pc.id]==nil else{throw ClientError.readOnly}
        let owner=epoch
        if let terminal=terminals[session.id]{try await terminal.terminate()}
        else{
            try relay.bindSession(session.id,pcID:pc.id){_ in};defer{relay.unbindSession(session.id)}
            let reply=try await relay.request(route:session.id,type:.terminate,response:.terminateResponse,capability:"host_history_v1"){id in var p=ProtoMessage();p.set(1,id.bytes);p.set(2,"USER_FORCE_CLOSE");return p}
            guard reply.uint(2)==1,try reply.text(3).isEmpty else{throw ClientError.unavailable("电脑尚未确认结束会话。")}
        }
        guard owner==epoch else{throw CancellationError()};closeView(session.id);activityStore.record("session-ended","结束了电脑上的「\(session.title.isEmpty ? session.shell:session.title)」");await refreshCatalogs()
    }
    func cleanup(pc:PC,sessionIDs:[UUID]) async throws {
        guard let relay,!sessionIDs.isEmpty,sessionIDs.count<=64 else{throw ClientError.limitExceeded}
        let reply=try await relay.request(route:pc.id,type:.cleanupRequest,response:.cleanupResponse,capability:"host_session_cleanup_v1"){ id in var p=ProtoMessage();p.set(1,id.bytes);sessionIDs.forEach{p.add(2,$0.bytes)};return p }
        guard try reply.text(3).isEmpty else{throw ClientError.unavailable("主机未确认历史清理。")};await refreshCatalogs()
    }
}
