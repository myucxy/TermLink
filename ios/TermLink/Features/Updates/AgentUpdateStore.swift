// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import Combine

@MainActor final class AgentUpdateStore: ObservableObject {
    @Published private(set) var tasks:[UUID:AgentUpdate]=[:]
    @Published private(set) var errors:[UUID:String]=[:]
    @Published var selected:UUID?
    @Published private(set) var offers:[UUID:JSONValue]=[:]
    private var retained:[UUID:String]=[:],hidden=Set<UUID>(),jobs:[UUID:Task<Void,Never>]=[:]
    private unowned let product:ProductStore
    private var epoch=UUID()
    private var checked:[UUID:Date]=[:]
    init(product:ProductStore){self.product=product}
    func restore(){do{retained=try product.privateState.load("agent-updates",as:[UUID:String].self) ?? [:];hidden=try product.privateState.load("hidden-update-errors",as:Set<UUID>.self) ?? []}catch{product.report(error)}}
    private func save(){do{try product.privateState.save(retained,name:"agent-updates");try product.privateState.save(hidden,name:"hidden-update-errors")}catch{product.report(error)}}
    func reset(){epoch=UUID();jobs.values.forEach{$0.cancel()};jobs=[:];retained=[:];tasks=[:];errors=[:];offers=[:];checked=[:];selected=nil;hidden=[]}
    func minimize(){selected=nil}
    func pause(){selected=nil;jobs.values.forEach{$0.cancel()}}
    func visibleError(_ pc:UUID)->String?{hidden.contains(pc) ? nil:errors[pc]}
    func eligibility(_ pc:UUID) async throws -> JSONValue {
        guard let api=product.api else{throw ClientError.authentication}
        let owner=epoch,value=try await api.request("/v1/pcs/\(pc)/agent-update-eligibility");guard owner==epoch else{throw CancellationError()};offers[pc]=value;checked[pc]=Date();return value
    }
    func install(_ pc:UUID,target:Int64) async {
        guard jobs[pc]==nil,target>0,let api=product.api else{return};let owner=epoch
        selected=nil;hidden.remove(pc);errors.removeValue(forKey:pc)
        jobs[pc]=Task{[weak self] in
            guard let self else{return};defer{if self.epoch==owner{self.jobs.removeValue(forKey:pc)}}
            do{let response=try await api.request("/v1/pcs/\(pc)/agent-updates",method:"POST",body:.object(["expected_target_build":.number(Double(target))]),idempotency:UUID());guard self.epoch==owner else{throw CancellationError()};let raw=response["agent_update"];try self.accept(raw,pc:pc);if !AgentUpdate(raw:raw,pcID:pc).terminal{try await self.watch(pc,id:raw["id"].string)}}catch is CancellationError{}catch{if self.epoch==owner{self.errors[pc]=error.localizedDescription}}
        }
    }
    private func accept(_ raw:JSONValue,pc:UUID) throws {
        guard UUID(uuidString:raw["id"].string) != nil,raw["pc_id"].string.lowercased()==pc.uuidString.lowercased() else{throw ClientError.invalidResponse("agent update identity")}
        let task=AgentUpdate(raw:raw,pcID:pc);hidden.remove(pc);errors.removeValue(forKey:pc)
        if task.status=="SUCCEEDED"{tasks.removeValue(forKey:pc);retained.removeValue(forKey:pc);offers.removeValue(forKey:pc);if selected==pc{selected=nil}}
        else{tasks[pc]=task;retained[pc]=task.id};save()
    }
    private func watch(_ pc:UUID,id:String) async throws {
        guard let api=product.api else{throw ClientError.authentication};let owner=epoch
        while !Task.isCancelled {
            do{let value=try await api.request("/v1/pcs/\(pc)/agent-updates/\(id)");guard owner==epoch else{throw CancellationError()};let task=AgentUpdate(raw:value["agent_update"],pcID:pc);guard task.id.lowercased()==id.lowercased() else{throw ClientError.invalidResponse("agent update changed")};try accept(task.raw,pc:pc);if task.terminal{return}}
            catch let e as APIError where [401,403,404].contains(e.status){throw e}
            catch is CancellationError{throw CancellationError()}
            catch{errors[pc]="暂时无法确认进度，正在重试。"}
            try await Task.sleep(for:.seconds(3))
        }
    }
    func recover() async {
        guard let api=product.api else{return};let owner=epoch
        for pc in product.pcs where pc.platform=="windows" && pc.capabilities.contains("remote_agent_update_v1") && jobs[pc.id]==nil {
            guard owner==epoch else{return}
            if pc.online,pc.routing,Date().timeIntervalSince(checked[pc.id] ?? .distantPast)>=300{
                checked[pc.id]=Date();_=try? await eligibility(pc.id);guard owner==epoch else{return}
            }
            if let id=retained[pc.id],tasks[pc.id]?.terminal != true{jobs[pc.id]=Task{[weak self] in guard let self else{return};defer{if self.epoch==owner{self.jobs.removeValue(forKey:pc.id)}};do{try await self.watch(pc.id,id:id)}catch is CancellationError{}catch{if self.epoch==owner{self.errors[pc.id]=error.localizedDescription}}}}
            else if retained[pc.id]==nil {do{let active=try await api.request("/v1/pcs/\(pc.id)/agent-updates/active");guard owner==epoch else{return};if active["agent_update"] != .null{try accept(active["agent_update"],pc:pc.id)}}catch{}}
        }
    }
    func dismiss(_ pc:UUID){if tasks[pc]?.terminal==true{tasks.removeValue(forKey:pc);retained.removeValue(forKey:pc)}else{hidden.insert(pc)};errors.removeValue(forKey:pc);if selected==pc{selected=nil};save()}
    func cancel(_ pc:UUID) async throws {guard let api=product.api,let task=tasks[pc],task.cancelable else{throw ClientError.readOnly};let owner=epoch,r=try await api.request("/v1/pcs/\(pc)/agent-updates/\(task.id)",method:"DELETE",idempotency:UUID());guard owner==epoch else{throw CancellationError()};try accept(r["agent_update"],pc:pc)}
}
