// SPDX-License-Identifier: GPL-3.0-only
import Combine
import Foundation

@MainActor final class AIArtifactDownloadStore: ObservableObject {
    typealias Confirm = (AIArtifactSource) async throws -> Void
    typealias Read = (AIArtifactSource, Int64) async throws -> JSONValue
    @Published private(set) var records: [AIArtifactDownload] = []
    @Published private(set) var available = false
    @Published var error: String?
    @Published var presented = false
    @Published private(set) var exportURL: URL?
    private var cache: AIArtifactDownloadCache?
    private var confirm: Confirm?
    private var read: Read?
    private var jobs: [UUID: Task<Void, Never>] = [:]
    private var stopping: [UUID: AIArtifactDownload.Status] = [:]
    private var epoch = UUID()
    private var retirement: Task<Void, Never>?
    private var modifying = Set<UUID>()
    private var starting = Set<AIArtifactSource>()
    private var sharing = false
    private var exportID: UUID?

    func activate(cache: AIArtifactDownloadCache, confirm: @escaping Confirm, read: @escaping Read) async throws {
        retire(); let owner = epoch
        await retirement?.value
        guard owner == epoch else { throw CancellationError() }
        self.cache = cache; self.confirm = confirm; self.read = read
        let saved = try await cache.restore()
        guard epoch == owner else { throw CancellationError() }
        records = saved.sorted { $0.createdAt > $1.createdAt }; available = true
    }
    func retire() {
        epoch = UUID()
        let previous = retirement, finishing = Array(jobs.values)
        finishing.forEach { $0.cancel() }
        retirement = Task { await previous?.value; for task in finishing { await task.value } }
        jobs = [:]; stopping = [:]; modifying = []; starting = []
        if let url = exportURL, let cache { Task { try? await cache.discardExport(url) } }
        cache = nil; confirm = nil; read = nil; records = []; available = false; error = nil
        presented = false; exportURL = nil; exportID = nil; sharing = false
    }
    func pauseAll() {
        for id in jobs.keys { stopping[id] = .paused; jobs[id]?.cancel() }
    }
    func isBusy(_ id: UUID) -> Bool { jobs[id] != nil || modifying.contains(id) }
    @discardableResult func start(source: AIArtifactSource) async throws -> UUID {
        guard available, let cache, jobs.count + starting.count < 3, !starting.contains(source) else { throw ClientError.unavailable("请先完成当前下载。") }
        let owner = epoch
        if let existing = records.first(where: { $0.source == source && $0.status.active }) {
            presented = true; return existing.id
        }
        starting.insert(source); defer { if epoch == owner { starting.remove(source) } }
        let record = try await cache.insert(source: source)
        guard epoch == owner else { throw CancellationError() }
        update(record); presented = true; launch(record.id); return record.id
    }
    func retry(_ id: UUID) {
        guard available, !isBusy(id), jobs.count + starting.count < 3, records.first(where: { $0.id == id })?.retryable == true else { return }
        launch(id)
    }
    private func launch(_ id: UUID) {
        guard let cache, let confirm, let read, let initial = records.first(where: { $0.id == id }), jobs[id] == nil else { return }
        let owner = epoch; stopping.removeValue(forKey: id)
        var visible = initial; visible.status = .preparing; visible.error = nil; update(visible)
        jobs[id] = Task { [weak self] in
            guard let self else { return }
            defer { if self.epoch == owner { self.jobs.removeValue(forKey: id); self.stopping.removeValue(forKey: id) } }
            do {
                // Re-query this exact PC/host/session before any resume request.
                try Task.checkCancellation(); try await confirm(initial.source)
                guard self.epoch == owner else { throw CancellationError() }; try Task.checkCancellation()
                var record = try await cache.resume(id)
                guard self.epoch == owner else { throw CancellationError() }; self.update(record)
                while record.status != .completed {
                    try Task.checkCancellation()
                    let response = try await read(initial.source, record.received)
                    guard self.epoch == owner else { throw CancellationError() }; try Task.checkCancellation()
                    let chunk = try AIArtifactChunk.decode(response, source: initial.source, offset: record.received, expectedSize: record.expectedBytes)
                    record = try await cache.append(id, chunk: chunk)
                    guard self.epoch == owner else { throw CancellationError() }; self.update(record)
                }
            } catch {
                let disposition = self.epoch == owner ? self.stopping[id] : nil
                let status = disposition ?? (error is CancellationError ? .paused : .failed)
                let message: String? = status == .failed ? "下载未完成。重新连接原会话后重试；文件或主机变化时请重新选择。" : nil
                // Retired tasks retain their original cache/account, but cannot publish into a new account.
                if let next = try? await cache.stop(id, status: status, error: message), self.epoch == owner { self.update(next) }
                else if self.epoch == owner { self.error = "无法保存下载进度；请重新打开下载列表确认。" }
            }
        }
    }
    func pause(_ id: UUID) async { await stop(id, status: .paused) }
    func cancel(_ id: UUID) async { await stop(id, status: .cancelled) }
    private func stop(_ id: UUID, status: AIArtifactDownload.Status) async {
        guard let cache else { return }; let owner = epoch
        if let task = jobs[id] {
            stopping[id] = status; task.cancel(); await task.value
        } else {
            do { let next = try await cache.stop(id, status: status); if epoch == owner { update(next) } }
            catch { if epoch == owner { self.error = "无法更新下载状态。" } }
        }
    }
    func remove(_ id: UUID) async {
        guard !isBusy(id), let cache else { return }; let owner = epoch
        modifying.insert(id); defer { if epoch == owner { modifying.remove(id) } }
        if exportID == id { dismissShare() }
        do { try await cache.remove(id); if epoch == owner { records.removeAll { $0.id == id } } }
        catch { if epoch == owner { self.error = "无法清理此下载，请稍后重试。" } }
    }
    func share(_ id: UUID) async {
        guard !sharing, !modifying.contains(id), exportURL == nil, records.first(where: { $0.id == id })?.status == .completed, let cache else { return }
        let owner = epoch; sharing = true; modifying.insert(id)
        defer { if epoch == owner { sharing = false; modifying.remove(id) } }
        do {
            let url = try await cache.export(id)
            guard epoch == owner else { try? await cache.discardExport(url); return }
            exportURL = url; exportID = id
        } catch { if epoch == owner { self.error = "下载缓存校验失败，未导出文件。" } }
    }
    func dismissShare() {
        guard let url = exportURL, let cache else { return }; exportURL = nil; exportID = nil
        Task { try? await cache.discardExport(url) }
    }
    private func update(_ record: AIArtifactDownload) {
        if let index = records.firstIndex(where: { $0.id == record.id }) { records[index] = record }
        else { records.insert(record, at: 0) }
    }
}
