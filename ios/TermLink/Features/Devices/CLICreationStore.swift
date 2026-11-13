// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import Combine

@MainActor final class CLICreationStore: ObservableObject {
    typealias Request = (String, String, JSONValue, UUID?) async throws -> JSONValue
    @Published private(set) var request: CLICreationRequest?
    @Published private(set) var recent: [CLIRecentDirectory] = []
    @Published private(set) var busy = false
    @Published private(set) var error: String?
    @Published private(set) var pcID: UUID?
    private let context: (UUID) throws -> CLICreationContext
    private let load: (String, String) throws -> CLICreationArchive?
    private let save: (CLICreationArchive, String, String) throws -> Void
    private let send: Request, now: () -> Date
    private let sleep: () async throws -> Void
    private let pollCount: Int
    private var epoch = UUID(), scope = "", archives: [UUID: CLICreationArchive] = [:]
    private var recoveryLocked = false
    init(context: @escaping (UUID) throws -> CLICreationContext,
         load: @escaping (String, String) throws -> CLICreationArchive?,
         save: @escaping (CLICreationArchive, String, String) throws -> Void,
         send: @escaping Request, now: @escaping () -> Date = Date.init,
         pollCount: Int = 12, sleep: @escaping () async throws -> Void = { try await Task.sleep(for: .milliseconds(500)) }) {
        self.context=context; self.load=load; self.save=save; self.send=send; self.now=now; self.pollCount=pollCount; self.sleep=sleep
    }
    convenience init(product: ProductStore) {
        self.init(context: { [unowned product] id in
            guard product.signedIn, !product.accountID.isEmpty, let requester=product.identity?.id,
                  let pc=product.pcs.first(where:{$0.id==id}), pc.canCreateTerminal, pc.keyEpoch>0,
                  product.devices.contains(where:{$0.id==pc.deviceID && $0.trust=="TRUSTED"}) else {
                throw ClientError.unavailable("电脑离线、远程访问已关闭或授权待确认。恢复后只能继续查询原请求。")
            }
            return .init(binding: .init(scope: product.privateState.scope, pcID: id, pcDeviceID: pc.deviceID,
                                       requesterID: requester, keyEpoch: pc.keyEpoch, platform: pc.platform), commands: pc.commands, canBrowse: pc.canBrowseFiles)
        }, load: { scope, name in try SecureState(scope: scope).load(name, as: CLICreationArchive.self) },
           save: { archive, scope, name in try SecureState(scope: scope).save(archive, name: name) },
           send: { [unowned product] path, method, body, key in
            guard let api=product.api else { throw ClientError.authentication }
            return try await api.request(path, method: method, body: body, idempotency: key)
        })
    }
    func reset() { epoch=UUID();busy=false;scope="";pcID=nil;request=nil;recent=[];archives=[:];error=nil;recoveryLocked=false }
    private func name(_ pc: UUID) -> String { "cli-creation-v1-" + pc.uuidString.lowercased() }
    @discardableResult func prepare(pc: UUID) throws -> CLICreationContext {
        let current = try context(pc)
        if scope != current.binding.scope { reset(); scope=current.binding.scope }
        guard !busy || pcID == pc else { throw ClientError.unavailable("另一台电脑的创建回执仍在查询中。") }
        if archives[pc] == nil {
            do {
                let stored = try load(scope, name(pc)) ?? CLICreationArchive()
                guard stored.version == 1, stored.recent.count <= 12 else { throw ClientError.invalidResponse("CLI archive") }
                if let saved=stored.request { try saved.validate(); guard saved.binding.scope==scope,saved.binding.pcID==pc else {throw ClientError.invalidResponse("CLI saved scope")} }
                archives[pc]=stored; recoveryLocked=false
            } catch { recoveryLocked=true;self.error="无法读取创建回执，已停止新建以避免重复终端。";throw error }
        }
        recoveryLocked=false
        pcID=pc;request=archives[pc]?.request
        recent=(archives[pc]?.recent ?? []).filter { $0.binding == current.binding && (try? CLIPath.validate($0.path,platform:current.binding.platform)) != nil }
        return current
    }
    func show(pc: UUID) { do { _=try prepare(pc:pc);error=nil } catch { self.error=error.localizedDescription } }
    var pending: Bool { request.map { !$0.complete } ?? false }
    var statusText: String {
        switch request?.status {
        case "SUCCEEDED": "电脑已确认创建会话。"
        case "FAILED": "电脑未能创建会话。"
        case "CANCELLED": "原请求已取消。"
        case "EXPIRED": "原请求已过期。"
        case "REJECTED": "服务器已拒绝此请求，未确认创建会话。"
        case "PENDING": "请求已收到，等待电脑处理。请查询原回执，不要重复创建。"
        case "PROCESSING": "电脑正在处理原请求。请继续查询回执。"
        case .some: "发现尚未确认的请求。请继续原请求，确认电脑回执；不会生成新的请求编号。"
        case .none: ""
        }
    }
    func clearCompleted(pc: UUID) throws {
        _=try prepare(pc:pc);guard !busy,!pending,!recoveryLocked else{throw ClientError.unavailable("未确认的请求不能清除或重新创建。")}
        var archive=archives[pc] ?? .init();archive.request=nil
        try save(archive,scope,name(pc));archives[pc]=archive;request=nil;error=nil
    }
    func create(pc: UUID, path: String, commandID: String) async throws -> UUID? {
        guard !busy else { throw ClientError.unavailable("正在查询原请求，请稍候。") }
        let current=try prepare(pc:pc)
        guard !pending,!recoveryLocked else { throw ClientError.unavailable("已有未确认的请求，请先查询原回执。") }
        try CLIPath.validate(path,platform:current.binding.platform)
        guard let command=current.commands.first(where:{$0["id"].string==commandID}) else{throw ClientError.readOnly}
        let submitted=CLICreationRequest(version:1,binding:current.binding,idempotency:UUID(),createdAt:now(),path:path,command:command,
            operationID:nil,status:"UNCONFIRMED",sessionID:nil,failure:nil)
        try submitted.validate()
        // Persist BEFORE sending. A storage failure must create no remote shell.
        try commit(submitted)
        return try await execute(submitted, owner:epoch)
    }
    func recover(pc: UUID) async throws -> UUID? {
        guard !busy else{throw ClientError.unavailable("正在查询原请求，请稍候。")}
        let current=try prepare(pc:pc)
        guard let saved=request else{throw ClientError.unavailable("没有需要查询的创建请求。")}
        try saved.requireAuthority(current)
        if saved.complete { return saved.sessionID }
        return try await execute(saved,owner:epoch)
    }
    private func commit(_ record: CLICreationRequest) throws {
        var archive=archives[record.binding.pcID] ?? .init();archive.request=record
        if record.status=="SUCCEEDED" {
            let item=CLIRecentDirectory(binding:record.binding,path:record.path)
            archive.recent=Array(([item]+archive.recent.filter{$0 != item}).prefix(12))
        }
        try save(archive,record.binding.scope,name(record.binding.pcID))
        archives[record.binding.pcID]=archive
        if pcID==record.binding.pcID { request=record;recent=archive.recent.filter{$0.binding==record.binding} }
    }
    private func execute(_ initial: CLICreationRequest, owner: UUID) async throws -> UUID? {
        busy=true;error=nil;defer{if owner==epoch{busy=false}}
        var current=initial
        func authorized() throws {
            try Task.checkCancellation();guard owner==epoch else{throw CancellationError()}
            try current.requireAuthority(context(current.binding.pcID))
        }
        do {
            try authorized()
            let response: JSONValue
            if let operation=current.operationID { response=try await send(current.route+"/\(operation)","GET",.null,nil) }
            else {
                guard current.canReplayOriginal(at:now()) else {
                    throw ClientError.unavailable("原请求已超过安全回执恢复时限或系统时间已改变。已保留记录；请在电脑确认会话，不能自动重新创建。")
                }
                // This is an explicit replay of the exact original request, not
                // a new create. The server may return its cached operation ID.
                response=try await send(current.route,"POST",current.body,current.idempotency)
            }
            try authorized();current=try current.applying(response);try commit(current)
            for _ in 0..<pollCount where !current.complete {
                try await sleep();try authorized()
                guard let operation=current.operationID else{throw ClientError.invalidResponse("CLI operation ID")}
                let response=try await send(current.route+"/\(operation)","GET",.null,nil)
                try authorized();current=try current.applying(response);try commit(current)
            }
            return current.sessionID
        } catch {
            if owner==epoch {
                if let api=error as? APIError, current.operationID==nil, api.status==400,
                   ["INVALID_FILE_OPERATION","INVALID_REQUEST","INVALID_PC_ID"].contains(api.code) {
                    current.status="REJECTED";current.failure=api.code;try commit(current)
                }
                self.error=error is CancellationError ? "查询已暂停，原请求已保留。" : error.localizedDescription
            }
            throw error
        }
    }
}
