// SPDX-License-Identifier: GPL-3.0-only
import Combine
import CryptoKit
import Foundation
import XCTest
@testable import TermLink

/// Opt-in integration with a disposable account and an explicitly selected test
/// directory. The config is external to the source tree and is never attached to
/// XCTest output. This exercises the real PKCE/token/device/Relay implementation;
/// it does not establish ASWebAuthenticationSession UI coverage.
@MainActor final class TermLinkLiveTests: XCTestCase {
    private struct Configuration: Decodable {
        let server: String
        let username: String
        let password: String
        let pc_id: String?
        let workspace_path: String?
        let expected_file_name: String?
        let expected_file_sha256: String?
        let zip_directory_name: String?
        let account_only: Bool?
        let large_file_name: String?
        let large_file_sha256: String?
        let preserve_login_for_ui: Bool?
        let components: [String]?
        let cleanup_operation_ids: [String]?
        let cleanup_session_ids: [String]?
        let empty_file_name: String?
        let empty_file_sha256: String?
        let ai_local_files: Bool?
        let ai_artifact_file_name: String?
        let ai_artifact_sha256: String?
        let ai_large_file_name: String?
        let ai_large_file_sha256: String?
        let require_agent_features_auth: Bool?
    }
    private struct Observation: Encodable {
        let step: String
        let status: String
        let detail: String
    }
    private enum Failure: Error { case rejected, timeout, invalidConfiguration }
    private var observations: [Observation] = []
    private var stage = "configuration"
    private var reportURL: URL?
    private var relayObservation: AnyCancellable?

    func testLiveMacAccountRelayTerminalAndFiles() async throws {
        guard let path = ProcessInfo.processInfo.environment["TERMLINK_LIVE_TEST_CONFIG"], !path.isEmpty else {
            throw XCTSkip("Requires TERMLINK_LIVE_TEST_CONFIG and a disposable live account; no live behavior was tested.")
        }
        let configuration: Configuration
        do {
            let data = try Data(contentsOf: URL(fileURLWithPath: path))
            guard data.count < 16_384 else { throw Failure.invalidConfiguration }
            configuration = try JSONDecoder().decode(Configuration.self, from: data)
            guard !configuration.username.isEmpty, !configuration.password.isEmpty else { throw Failure.invalidConfiguration }
        } catch { throw Failure.invalidConfiguration }
        reportURL = URL(fileURLWithPath: path).deletingLastPathComponent().appendingPathComponent("termlink-live-results.json")
        let product = ProductStore()
        do {
            stage = "authorization_code_pkce"
            let root = try APIClient.origin(configuration.server)
            let tokens = try await authenticate(configuration, root: root)
            record("passed", "Real authorization code, S256 PKCE, state and exact iOS callback validated; no UI login claim.")
            stage = "token_refresh_and_keychain"
            let tokenStore = try TokenStore(scope: root.absoluteString)
            try await tokenStore.save(tokens)
            try await tokenStore.refresh(rejected: tokens.access)
            guard await tokenStore.isSignedIn() else { throw Failure.rejected }
            record("passed", "Production TokenStore refresh and simulator Keychain persistence.")
            stage = "ios_registration_and_relay"
            product.server = root.absoluteString
            await product.restore()
            guard product.signedIn, let api = product.api, let relay = product.relay,
                  relay.state == .connected, let identity = product.identity else { throw Failure.rejected }
            relayObservation = relay.$state.sink { [weak self] state in
                if case .failed(let reason) = state { self?.record("relay-diagnostic", reason) }
            }
            let device = try await api.request("/v1/devices/self")
            guard device["device"]["id"].string.lowercased() == identity.id.uuidString.lowercased(),
                  device["device"]["type"].string == "IOS", device["device"]["trust_state"].string == "TRUSTED" else { throw Failure.rejected }
            record("passed", "IOS device proof, trusted self identity and authenticated WSS handshake.")
            if configuration.account_only == true {
                stage = "account_only_mode"
                record("not-run", "Explicit account-only run; Mac runtime operations deferred until host is ready.")
                if configuration.preserve_login_for_ui == true {
                    stage = "ui_login_handoff"
                    product.background()
                    record("prepared", "Only the disposable test account is retained for the explicitly authorized UI handoff; no production user account is selected.")
                    try writeReport()
                    return
                }
                stage = "sign_out"
                await product.signOut()
                let persisted = try TokenStore(scope: root.absoluteString)
                guard !product.signedIn, !(await persisted.isSignedIn()) else { throw Failure.rejected }
                record("passed", "Account disconnected and test-account Keychain tokens removed.")
                try writeReport()
                return
            }
            stage = "mac_discovery_and_capabilities"
            let candidates = product.pcs.filter { $0.platform == "macos" && $0.online && $0.routing }
            let pc: PC
            if let requested = configuration.pc_id {
                guard let matched = candidates.first(where: { $0.id.uuidString.lowercased() == requested.lowercased() }) else { throw Failure.rejected }
                pc = matched
            } else {
                guard candidates.count == 1, let matched = candidates.first else { throw Failure.rejected }
                pc = matched
            }
            guard pc.canCreateTerminal, pc.canBrowseFiles, !pc.commands.isEmpty else { throw Failure.rejected }
            record("passed", "Mac online/routing, POSIX commands, terminal creation and file browsing advertised.")
            for raw in configuration.cleanup_operation_ids ?? [] {
                guard let id = UUID(uuidString: raw) else { throw Failure.invalidConfiguration }
                stage = "prior_test_operation_cleanup"
                _ = try await api.request("/v1/pcs/\(pc.id)/file-operations/\(id)", method: "DELETE", idempotency: UUID())
                record("passed", "Cancelled only an explicitly listed prior test operation through the normal authenticated API.")
            }
            stage = "encrypted_session_catalog"
            _ = try await relay.catalog(pc: pc.id)
            record("passed", "Authenticated host session catalog decrypted by the production iOS Relay client.")
            for raw in configuration.cleanup_session_ids ?? [] {
                guard let id = UUID(uuidString: raw) else { throw Failure.invalidConfiguration }
                await product.refreshCatalogs()
                if let prior = product.sessions.first(where: { $0.id == id && $0.pcID == pc.id && $0.lifecycle == "RUNNING" }) {
                    stage = "prior_test_session_cleanup"
                    try await product.terminate(prior)
                    record("passed", "Terminated only an explicitly listed prior test session through the normal authenticated attach/terminate route.")
                }
            }
            stage = "ai_catalog_read_only"
            if pc.capabilities.contains("ai_agent_v1") {
                do {
                    try await product.ai.refresh(pc.id)
                    guard let catalog=product.ai.catalogs[pc.id] else{throw Failure.rejected}
                    let brands=catalog["agents"].array.map{$0["id"].string}.filter{!$0.isEmpty}.sorted()
                    record("passed","Read-only agent IDs: "+brands.joined(separator:",")+"; no model calls.")
                } catch {record("diagnostic",safeError(error))}
            } else {record("host-not-advertised","Host does not advertise ai_agent_v1.")}
            if let workspace = configuration.workspace_path, workspace.hasPrefix("/"), !workspace.contains("\0") {
                let components = configuration.components ?? ["files", "terminal"]
                if components.contains("files") {
                    do { try await verifyFiles(configuration, product: product, pc: pc, workspace: workspace) }
                    catch { record("failed", safeError(error)); XCTFail("Live file scenario failed at \(stage): \(safeError(error)).") }
                }
                if components.contains("terminal") {
                    do { try await verifyTerminal(product: product, pc: pc, workspace: workspace) }
                    catch { record("failed", safeError(error)); XCTFail("Live terminal scenario failed at \(stage): \(safeError(error)).") }
                }
                if configuration.ai_local_files == true {
                    do { try await verifyAI(configuration,product:product,pc:pc,workspace:workspace) }
                    catch { record("failed",safeError(error));XCTFail("Live AI scenario failed at \(stage): \(safeError(error)).") }
                }
            } else {
                stage = "mac_fixture_operations"
                record("not-run", "Requires an explicitly supplied disposable workspace_path.")
            }
            stage = "sign_out"
            await product.signOut()
            let persisted = try TokenStore(scope: root.absoluteString)
            guard !product.signedIn, !(await persisted.isSignedIn()) else { throw Failure.rejected }
            record("passed", "Account disconnected and only this simulator's test-account tokens removed.")
        } catch {
            let code: String
            if let api = error as? APIError { code = "HTTP \(api.status) " + api.code }
            else if error is CancellationError { code = "cancelled" }
            else { code = safeError(error) }
            record("failed", code)
            await product.signOut()
            XCTFail("Live integration failed at \(stage): \(code). Credentials and tokens are omitted.")
        }
        try writeReport()
    }

    private func record(_ status: String, _ detail: String) {
        observations.append(.init(step: stage, status: status, detail: detail))
        try? writeReport()
    }
    private func safeError(_ error: Error) -> String {
        if let failure = error as? Failure { return String(describing: failure) }
        if let api = error as? APIError { return "HTTP \(api.status) " + api.code }
        if case ClientError.protocolViolation(let value) = error { return "protocol: " + value }
        if case ClientError.unavailable(let value) = error { return value }
        return String(describing: type(of: error))
    }
    private func inspectFailedDownload(_ product: ProductStore, pc: PC, id: UUID) async {
        guard let download = product.files.downloads.first(where: { $0.id == id }) else { return }
        let code: String
        if download.error?.contains("超时") == true { code = "timeout" }
        else if download.error?.contains("协议") == true { code = "protocol_validation" }
        else { code = "unclassified" }
        var detail = "download=\(download.status), received=\(download.received), expected=\(download.expectedBytes), error_kind=\(code), operation=\(download.operationID.uuidString.lowercased())"
        if let error = download.error, ["文件操作结果待确认。", "文件传输超时，可重新查询并续传。", "电脑停止了文件传输。"].contains(error) { detail += "; reason=" + error }
        if let api = product.api, let value = try? await api.request("/v1/pcs/\(pc.id)/file-operations/\(download.operationID)") {
            detail += ", server_status=" + value["file_operation"]["status"].string
        }
        record("diagnostic", detail)
    }
    private func writeReport() throws {
        guard let reportURL else { return }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(observations).write(to: reportURL, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: reportURL.path)
    }
    private func wait(seconds: Double = 45, until condition: () -> Bool) async throws {
        let end = Date().addingTimeInterval(seconds)
        while !condition() {
            guard Date() < end else { throw Failure.timeout }
            try await Task.sleep(for: .milliseconds(200))
        }
    }
    private func verifyFiles(_ configuration: Configuration, product: ProductStore, pc: PC, workspace: String) async throws {
        stage = "file_listing_and_favorites"
        await product.files.list(pc: pc.id, path: workspace)
        guard product.files.error == nil, product.files.path == workspace else { throw Failure.rejected }
        product.files.toggleFavorite()
        guard product.files.currentFavorites.contains(workspace) else { throw Failure.rejected }
        product.files.toggleFavorite()
        record("passed", "Selected Mac test directory listed; account-scoped favorite toggled and restored.")
        stage = "file_search_and_missing_path"
        if let name=configuration.expected_file_name {
            product.files.query=name
            await product.files.list(pc:pc.id,path:workspace)
            guard product.files.error==nil,product.files.entries.contains(where:{$0.name==name}),
                  product.files.entries.allSatisfy({$0.name.localizedCaseInsensitiveContains(name)}) else{throw Failure.rejected}
            product.files.query=""
            await product.files.list(pc:pc.id,path:workspace)
        }
        var missingRejected=false
        do { _=try await product.files.operation(pc:pc.id,kind:"LIST",path:workspace+"/missing-"+UUID().uuidString) }
        catch { missingRejected=true }
        guard missingRejected else{throw Failure.rejected}
        record("passed", "Name filter returned the fixture and a nonexistent child directory was rejected by the host.")
        if let filename=configuration.empty_file_name,let digest=configuration.empty_file_sha256,
           let entry=product.files.entries.first(where:{$0.name==filename && !$0.directory}) {
            stage="empty_file_download"
            try await product.files.download(entry,pc:pc)
            guard let id=product.files.downloads.last?.id else{throw Failure.rejected}
            try await wait(seconds:90){product.files.downloads.contains{$0.id==id && ["COMPLETED","FAILED"].contains($0.status)}}
            guard let item=product.files.downloads.first(where:{$0.id==id}),item.status=="COMPLETED" else{await inspectFailedDownload(product,pc:pc,id:id);throw Failure.rejected}
            let data=try Data(contentsOf:product.files.localFile(item))
            guard data.isEmpty,Data(SHA256.hash(data:data)).hex==digest.lowercased() else{throw Failure.rejected}
            record("passed","Zero-byte offer/resume/complete ACK completed through real encrypted transport with empty SHA-256.")
            product.files.removeRecord(id)
        }
        if let filename = configuration.expected_file_name, let digest = configuration.expected_file_sha256,
           let entry = product.files.entries.first(where: { $0.name == filename && !$0.directory }) {
            stage = "file_download_sha256"
            try await product.files.download(entry, pc: pc)
            guard let id = product.files.downloads.last?.id else { throw Failure.rejected }
            try await wait(seconds: 90) { product.files.downloads.contains { $0.id == id && ["COMPLETED", "FAILED"].contains($0.status) } }
            guard let download = product.files.downloads.first(where: { $0.id == id }), download.status == "COMPLETED" else {
                await inspectFailedDownload(product, pc: pc, id: id)
                throw Failure.rejected
            }
            let data = try Data(contentsOf: product.files.localFile(download))
            guard Data(SHA256.hash(data: data)).hex == digest.lowercased() else { throw Failure.rejected }
            record("passed", "Production encrypted file-transfer.v2 download matches independent fixture SHA-256.")
            product.files.removeRecord(id)
        } else {
            stage = "file_download_sha256"; record("not-run", "No named fixture with independent SHA-256 was supplied.")
        }
        if let directory = configuration.zip_directory_name,
           let entry = product.files.entries.first(where: { $0.name == directory && $0.directory }) {
            stage = "directory_zip_download"
            try await product.files.download(entry, pc: pc)
            guard let id = product.files.downloads.last?.id else { throw Failure.rejected }
            try await wait(seconds: 90) { product.files.downloads.contains { $0.id == id && ["COMPLETED", "FAILED"].contains($0.status) } }
            guard let download = product.files.downloads.first(where: { $0.id == id }), download.status == "COMPLETED", download.directoryZIP else {
                await inspectFailedDownload(product, pc: pc, id: id)
                throw Failure.rejected
            }
            let data = try Data(contentsOf: product.files.localFile(download))
            guard data.starts(with: [0x50, 0x4b]) else { throw Failure.rejected }
            record("passed", "Host directory ZIP transferred and validated by the production receiver; ZIP file signature checked.")
            product.files.removeRecord(id)
        } else { stage = "directory_zip_download"; record("not-run", "No named ZIP fixture was supplied.") }
        if let name = configuration.large_file_name, let digest = configuration.large_file_sha256,
           let entry = product.files.entries.first(where: { $0.name == name && !$0.directory }) {
            try await verifyResumeAndCancel(entry, digest: digest, product: product, pc: pc)
        } else { stage = "file_resume_and_cancel"; record("not-run", "No larger disposable fixture was supplied.") }
    }
    private func verifyResumeAndCancel(_ entry: RemoteEntry, digest: String, product: ProductStore, pc: PC) async throws {
        stage = "partial_file_resume"
        try await product.files.download(entry, pc: pc)
        guard let initial = product.files.downloads.last else { throw Failure.rejected }
        let deadline = Date().addingTimeInterval(90)
        while !product.files.downloads.contains(where: { $0.id == initial.id && ($0.received > 0 || $0.status == "FAILED") }) {
            guard Date() < deadline else { throw Failure.timeout }
            try await Task.sleep(for: .milliseconds(10))
        }
        product.files.pause()
        try await wait { product.files.downloads.contains { $0.id == initial.id && ["RESUMING", "COMPLETED", "FAILED"].contains($0.status) } }
        guard let paused = product.files.downloads.first(where: { $0.id == initial.id }), paused.status != "FAILED" else { throw Failure.rejected }
        if paused.status == "COMPLETED" {
            record("not-exercised", "Transfer completed before cancellation; no resume claim.")
        } else {
            guard paused.received > 0 else { throw Failure.rejected }
            product.files.retry(initial.id)
            try await wait(seconds: 180) { product.files.downloads.contains { $0.id == initial.id && ["COMPLETED", "FAILED"].contains($0.status) } }
            guard let resumed = product.files.downloads.first(where: { $0.id == initial.id }),
                  resumed.status == "COMPLETED", resumed.operationID == initial.operationID else { throw Failure.rejected }
            let data = try Data(contentsOf: product.files.localFile(resumed))
            guard Data(SHA256.hash(data: data)).hex == digest.lowercased() else { throw Failure.rejected }
            record("passed", "Partially received file resumed using the same operation ID and matched the independent full SHA-256.")
        }
        product.files.removeRecord(initial.id)
        stage = "download_operation_cancel"
        try await product.files.download(entry, pc: pc)
        guard let cancel = product.files.downloads.last else { throw Failure.rejected }
        product.files.cancel(cancel.id)
        do { try await wait { product.files.downloads.contains { $0.id == cancel.id && $0.status == "CANCELLED" } } }
        catch { await inspectFailedDownload(product,pc:pc,id:cancel.id);throw error }
        guard let api = product.api else { throw Failure.rejected }
        let state = try await api.request("/v1/pcs/\(pc.id)/file-operations/\(cancel.operationID)")
        guard state["file_operation"]["status"].string == "CANCELLED" else {
            await inspectFailedDownload(product,pc:pc,id:cancel.id)
            throw Failure.rejected
        }
        record("passed", "Explicit cancellation was confirmed by both local download state and the server operation record.")
        product.files.removeRecord(cancel.id)
    }
    private func verifyTerminal(product: ProductStore, pc: PC, workspace: String) async throws {
        stage = "directory_cli_creation"
        guard let command = pc.commands.first(where: { ["zsh", "bash"].contains($0["shell"].string) || ["zsh", "bash"].contains($0["id"].string) }) ?? pc.commands.first else { throw Failure.rejected }
        let result = try await product.files.operation(pc: pc.id, kind: "OPEN_TERMINAL", path: workspace,
            extra: ["terminal_command_id": .string(command["id"].string)])
        guard let id = UUID(uuidString: result["result"]["session_id"].string) else { throw Failure.rejected }
        await product.refreshCatalogs()
        guard let session = product.sessions.first(where: { $0.id == id && $0.pcID == pc.id }) else { throw Failure.rejected }
        record("passed", "Actual Mac session created in the explicit fixture directory and found in host catalog.")
        do {
            stage = "terminal_attach_snapshot"
            await product.open(session)
            guard let terminal = product.terminals[id] else { throw Failure.rejected }
            try await wait(seconds: 90) { terminal.isSynchronized || terminal.error != nil }
            guard terminal.isSynchronized, terminal.error == nil else {
                record("diagnostic", "terminal_status=" + terminal.status + "; terminal_error=" + (terminal.error ?? "none"))
                throw Failure.rejected
            }
            record("passed", "Native core imported the real recipient-sealed host snapshot and produced a validated frame.")
            stage = "lease_and_cli_input"
            try await terminal.requestControl()
            guard terminal.canInput else { throw Failure.rejected }
            let marker = "TL_IOS_LIVE_" + UUID().uuidString.replacingOccurrences(of: "-", with: "")
            let split = marker.index(marker.startIndex, offsetBy: 10)
            let command = "printf '%s%s\\n' '" + marker[..<split] + "' '" + marker[split...] + "'\n"
            try await terminal.text(command)
            try await wait { terminal.frame?.cells.map(\.text).joined().contains(marker) == true || terminal.error != nil }
            guard terminal.error == nil, terminal.canInput else { throw Failure.rejected }
            record("passed", "Lease acquired and a harmless printf traveled through typed encrypted input into the actual Mac PTY.")
            stage = "terminal_resize_and_restore"
            let size = try await terminal.resize(columns: 100, rows: 30, cellWidth: 8, cellHeight: 16)
            guard size.columns == 100, size.rows == 30 else { throw Failure.rejected }
            _ = try await terminal.resize(columns: 0, rows: 0, cellWidth: 0, cellHeight: 0, restore: true)
            record("passed", "Phone-size and restore controls received authoritative host responses.")
            stage = "frozen_history_input_guard"
            terminal.browseHistory(by: 1)
            guard terminal.frozen, !terminal.canInput else { throw Failure.rejected }
            var rejected = false
            do { try await terminal.text("this_must_not_be_sent") } catch { rejected = true }
            guard rejected else { throw Failure.rejected }
            terminal.endHistory()
            guard !terminal.frozen, terminal.canInput else { throw Failure.rejected }
            try await terminal.releaseControl()
            record("passed", "Frozen native history blocked input, returned to live frame and released the lease.")
            stage = "foreground_reconnect"
            product.background()
            guard product.relay?.state == .disconnected else { throw Failure.rejected }
            await product.foreground()
            guard product.relay?.state == .connected, let reopened = product.terminals[id] else { throw Failure.rejected }
            try await wait(seconds: 90) { reopened.isSynchronized || reopened.error != nil }
            guard reopened.isSynchronized, reopened.error == nil else { throw Failure.rejected }
            record("passed", "Background disconnect and foreground reconnect reopened the same host session with a fresh snapshot.")
            stage = "same_connection_reopen"
            product.closeView(id)
            await product.open(session)
            guard let sameSocket = product.terminals[id] else { throw Failure.rejected }
            try await wait(seconds:90) { sameSocket.isSynchronized || sameSocket.error != nil }
            guard sameSocket.isSynchronized,sameSocket.error == nil,product.relay?.state == .connected else { throw Failure.rejected }
            record("passed", "After ACK > 0, close/reopen reset the same WebSocket subscription and synchronized without regressing server ACK.")
            stage = "unopened_session_termination"
            product.closeView(id)
            try await product.terminate(session)
            guard product.sessions.first(where: { $0.id == id })?.lifecycle != "RUNNING" else { throw Failure.rejected }
            record("passed", "Closed UI session was attached temporarily, terminated by the host and catalog-confirmed.")
        } catch {
            // Cleanup is restricted to the session created by this exact test.
            product.closeView(id)
            do { try await product.terminate(session); record("cleanup", "Only the session created by this test was terminated.") }
            catch { record("cleanup-failed", "Created test session termination requires manual follow-up: " + id.uuidString.lowercased()) }
            throw error
        }
    }

    private func verifyAI(_ configuration: Configuration, product: ProductStore, pc: PC, workspace: String) async throws {
        stage="ai_five_brand_runtime_descriptors"
        try await product.ai.refresh(pc.id)
        guard let catalog=product.ai.catalogs[pc.id] else{throw Failure.rejected}
        let ids=Set(catalog["agents"].array.map{$0["id"].string})
        guard Set(["codex","claude","opencode","grok","zcode"]).isSubset(of:ids) else{throw Failure.rejected}
        let allowedStatus=Set(["available","missing","detected","incompatible","probe_failed","runtime_missing","runtime_blocked"])
        for agent in catalog["agents"].array {
            guard allowedStatus.contains(agent["status"].string) else{throw Failure.rejected}
            let auth=agent["runtimeAuthentication"]
            guard auth["version"].integer==1,["isolated_required","not_checked"].contains(auth["status"].string),
                  ["device_code","host_setup"].contains(auth["method"].string) else{throw Failure.rejected}
            // Record status only; never serialize provider paths, codes or account identities.
            record("observed",agent["id"].string+": "+agent["status"].string+"; auth="+auth["status"].string+"; method="+auth["method"].string)
        }
        stage="codex_read_only_authentication"
        guard let codex=catalog["agents"].array.first(where:{$0["id"].string=="codex"}),codex["status"].string=="available" else {
            record("blocked","Codex CLI protocol is unavailable; no authentication or thread creation attempted.");return
        }
        do {
            let auth=try await product.ai.runtimeAuthentication(pc:pc.id,agent:"codex",action:"authentication")
            guard ["required","not_required","authenticated","unknown"].contains(auth["status"].string),auth["isolated"].bool,
                  !auth["modelAccessVerified"].bool,auth["userCode"].string.isEmpty,auth["verificationUrl"].string.isEmpty else{throw Failure.rejected}
            record("passed","Read-only isolated Codex account status="+auth["status"].string+"; no device-code challenge, login, model inference or token contents.")
        } catch {
            record("blocked","Read-only runtime authentication unavailable: "+safeError(error))
        }
        stage="codex_thread_only_create"
        let before=Set(product.ai.conversations.keys)
        do {try await product.ai.create(pc:pc.id,agent:"codex",directory:workspace,options:["directoryMode":.string("custom"),"permissionMode":.string("ask")])}
        catch {
            record("blocked","Thread-only create was rejected; no model message sent: "+safeError(error))
            for created in product.ai.conversations.values where !before.contains(created.id) && created.pcID==pc.id {
                _=try? await product.ai.mutate(pc:pc.id,action:"end",session:created)
            }
            return
        }
        guard let id=product.ai.selected,!before.contains(id),let session=product.ai.conversations[id],session.pcID==pc.id,
              session.raw["agentId"].string=="codex",session.directory==workspace else{throw Failure.rejected}
        do {
            guard session.state=="idle",session.messages.isEmpty,session.pending.isEmpty,session.canControl else{throw Failure.rejected}
            guard session.capabilities["hostPlatform"].string=="macos",session.capabilities["pathStyle"].string=="posix",
                  session.capabilities["attachments"].bool,session.capabilities["artifacts"].bool,session.capabilities["fileReferences"].bool else{throw Failure.rejected}
            record("passed","Created one isolated Codex thread in the authorized fixture directory; no turn/start, send, review, fork, revert or tool execution.")
            stage="ai_agent_features_read_only"
            do {
                let features=try await product.ai.read(session,action:"agent.features")
                guard features["agentFeatures"]["agent"].string=="codex",features["agentFeatures"]["features"].array.count<=100 else{throw Failure.rejected}
                if configuration.require_agent_features_auth == true {
                    let rows=features["agentFeatures"]["features"].array
                    guard rows.first(where:{$0["id"].string=="rateLimits"})?["status"].string=="requires_auth",
                          rows.first(where:{$0["id"].string=="skills"})?["status"].string=="available",
                          rows.first(where:{$0["id"].string=="mcp"})?["status"].string=="available" else{throw Failure.rejected}
                    record("passed","Real Codex skills/MCP metadata remains available while rateLimits is explicitly requires_auth; no account data or model execution.")
                } else {
                    record("passed","Read bounded capabilities/skills/tool metadata from this test thread only; no general RPC forwarding or model execution.")
                }
            } catch {
                if configuration.require_agent_features_auth == true {throw error}
                record("blocked","Agent feature metadata request was rejected: "+safeError(error)+"; continuing independent attachment/artifact checks without sending a model request.")
            }
            stage="ai_attachment_upload_remove"
            let local=FileManager.default.temporaryDirectory.appendingPathComponent("termlink-live-attachment-"+UUID().uuidString+".txt")
            let payload=Data(String(repeating:"TermLink AI attachment fixture 中文\n",count:2500).utf8)
            try payload.write(to:local,options:[.atomic,.completeFileProtection]);defer{try? FileManager.default.removeItem(at:local)}
            try await product.ai.upload(session,url:local)
            guard let attachment=product.ai.attachments[id]?.last,attachment.status=="ready",attachment.sent==Int64(payload.count),
                  attachment.digest==Data(SHA256.hash(data:payload)).hex else{throw Failure.rejected}
            let uploadedID=attachment.id
            try await product.ai.removeAttachment(session,id:uploadedID)
            guard product.ai.attachments[id]?.contains(where:{$0.id==uploadedID}) != true else{throw Failure.rejected}
            record("passed","Production iOS uploader performed begin/chunk/commit with SHA-256, then removed only its unused test attachment; attachment was never sent to a model.")
            stage="ai_artifact_catalog"
            let listing=try await product.ai.read(session,action:"artifacts",extra:["text":.string(""),"offset":.number(0)])
            let artifacts=listing["artifacts"]["files"].array
            guard !artifacts.isEmpty,artifacts.count<=200 else{throw Failure.rejected}
            guard let small=configuration.ai_artifact_file_name ?? configuration.expected_file_name,
                  let digest=configuration.ai_artifact_sha256 ?? configuration.expected_file_sha256,
                  let item=artifacts.first(where:{$0["id"].string==small}),!item["version"].string.isEmpty else{throw Failure.rejected}
            record("passed","Host artifact list includes the explicitly named fixture with a version; arbitrary user files are not read.")
            stage="ai_artifact_persistent_download"
            try await downloadAIArtifact(session:session,item:item,digest:digest,product:product,pause:false)
            if let name=configuration.ai_large_file_name,let digest=configuration.ai_large_file_sha256,
               let item=artifacts.first(where:{$0["id"].string==name}) {
                stage="ai_artifact_pause_resume"
                try await downloadAIArtifact(session:session,item:item,digest:digest,product:product,pause:true)
            } else {stage="ai_artifact_pause_resume";record("not-run","No named AI artifact large fixture supplied.")}
            stage="codex_thread_cleanup"
            try await endCreatedAI(session,product:product)
            try await product.ai.refresh(pc.id)
            guard product.ai.conversations[id]?.state != "running" else{throw Failure.rejected}
            record("passed","Ended only the Codex session created by this test; no prompt or model request was sent.")
        } catch {
            let failing=stage
            do {try await endCreatedAI(session,product:product)}
            catch {record("cleanup-failed","Only this created AI session needs cleanup: "+session.id)}
            stage=failing;throw error
        }
    }
    private func endCreatedAI(_ session:AIConversation,product:ProductStore) async throws {
        try await product.ai.refresh(session.pcID,session:session.id)
        guard var current=product.ai.conversations[session.id],current.pcID==session.pcID else{throw Failure.rejected}
        if ["exited","interrupted"].contains(current.state){return}
        if !current.canControl {
            _=try await product.ai.mutate(pc:session.pcID,action:"control",session:current)
            guard let updated=product.ai.conversations[session.id] else{throw Failure.rejected};current=updated
        }
        _=try await product.ai.mutate(pc:session.pcID,action:"end",session:current)
    }
    private func downloadAIArtifact(session:AIConversation,item:JSONValue,digest:String,product:ProductStore,pause:Bool) async throws {
        let store=product.ai.artifactDownloads,before=Set(store.records.map(\.id))
        try await product.ai.downloadArtifact(session,file:item["id"].string,version:item["version"].string)
        guard let record=store.records.first(where:{!before.contains($0.id)}) else{throw Failure.rejected}
        let id=record.id
        do {
            if pause {
                let end=Date().addingTimeInterval(90)
                while store.records.first(where:{$0.id==id})?.received == 0 {
                    guard Date()<end,store.records.first(where:{$0.id==id})?.status != .failed else{throw Failure.rejected}
                    try await Task.sleep(for:.milliseconds(5))
                }
                await store.pause(id)
                guard let partial=store.records.first(where:{$0.id==id}) else{throw Failure.rejected}
                if partial.status == .completed {self.record("not-exercised","Artifact finished before pause; no partial resume claim.")}
                else {
                    guard partial.status == .paused,partial.received>0,partial.received<(partial.expectedBytes ?? 0) else{throw Failure.rejected}
                    let saved=partial.received
                    store.retry(id)
                    try await wait(seconds:180){store.records.first(where:{$0.id==id})?.status == .completed || store.records.first(where:{$0.id==id})?.status == .failed}
                    guard store.records.first(where:{$0.id==id})?.received ?? 0 >= saved else{throw Failure.rejected}
                }
            } else {
                try await wait(seconds:120){store.records.first(where:{$0.id==id})?.status == .completed || store.records.first(where:{$0.id==id})?.status == .failed}
            }
            guard let completed=store.records.first(where:{$0.id==id}),completed.status == .completed,completed.source==record.source,
                  completed.received==item["size"].integer else{throw Failure.rejected}
            await store.share(id)
            guard let url=store.exportURL else{throw Failure.rejected}
            let exported=try await FileDigest.sha256(url,size:UInt64(completed.received),limit:50*1024*1024)
            guard exported.hex==digest.lowercased() else{throw Failure.rejected}
            store.dismissShare();await store.remove(id)
            guard !store.records.contains(where:{$0.id==id}) else{throw Failure.rejected}
            self.record("passed",pause ? "Same task/source resumed from a partial encrypted cache; explicit export matches independent fixture SHA-256. Host protocol only asserts version/size." : "Persistent production artifact download/export matches independent fixture SHA-256; exported temporary file and task were cleaned.")
        } catch {
            await store.cancel(id);store.dismissShare();await store.remove(id);throw error
        }
    }

    private func authenticate(_ credentials: Configuration, root: URL) async throws -> TokenSet {
        let configuration = try await ClientConfiguration.load(root: root)
        let discovery = try await configuration.discovery()
        let verifier = try SessionCrypto.random(32).base64URL, state = try SessionCrypto.random(32).base64URL
        guard var authorize = URLComponents(string: discovery["authorization_endpoint"].string),
              let tokenEndpoint = URL(string: discovery["token_endpoint"].string) else { throw Failure.rejected }
        authorize.queryItems = [.init(name: "response_type", value: "code"), .init(name: "client_id", value: configuration.clientID),
            .init(name: "redirect_uri", value: configuration.redirect.absoluteString), .init(name: "scope", value: "openid profile email offline_access"),
            .init(name: "state", value: state), .init(name: "code_challenge_method", value: "S256"),
            .init(name: "code_challenge", value: Data(SHA256.hash(data: Data(verifier.utf8))).base64URL)]
        guard let authorizationURL = authorize.url else { throw Failure.rejected }
        let sessionConfig = URLSessionConfiguration.ephemeral
        sessionConfig.timeoutIntervalForRequest = 30
        let network = URLSession(configuration: sessionConfig, delegate: NoRedirectDelegate(), delegateQueue: nil)
        defer { network.invalidateAndCancel() }
        var request = URLRequest(url: authorizationURL)
        var submitted = false
        for _ in 0..<10 {
            guard let url = request.url, sameOrigin(url, root) else { throw Failure.rejected }
            stage = submitted ? "authorization_form_response" : "authorization_page"
            let (data, response) = try await network.data(for: request)
            guard data.count < 1_048_576, let http = response as? HTTPURLResponse else { throw Failure.rejected }
            if (300..<400).contains(http.statusCode), let location = http.value(forHTTPHeaderField: "Location"),
               let target = URL(string: location, relativeTo: url)?.absoluteURL {
                if target.scheme == configuration.redirect.scheme {
                    let fields = URLComponents(url: target, resolvingAgainstBaseURL: false)?.queryItems ?? []
                    guard submitted, target.scheme == configuration.redirect.scheme,
                          target.host == configuration.redirect.host, target.path == configuration.redirect.path,
                          fields.filter({ $0.name == "state" }).count == 1,
                          fields.first(where: { $0.name == "state" })?.value == state,
                          fields.filter({ $0.name == "code" }).count == 1,
                          let code = fields.first(where: { $0.name == "code" })?.value, !code.isEmpty else { throw Failure.rejected }
                    stage = "authorization_code_exchange"
                    return try await TokenStore.exchange(endpoint: tokenEndpoint, configuration: configuration,
                        fields: ["grant_type": "authorization_code", "code": code, "client_id": configuration.clientID,
                                 "redirect_uri": configuration.redirect.absoluteString, "code_verifier": verifier])
                }
                guard sameOrigin(target, root) else { throw Failure.rejected }
                request = URLRequest(url: target)
                continue
            }
            guard http.statusCode == 200, !submitted, let html = String(data: data, encoding: .utf8) else { throw Failure.rejected }
            stage = "authorization_form_parse"
            let forms = try matches("<form\\b[^>]*>", in: html)
            guard let form = forms.first(where: { (try? attribute("id", from: $0)) == "kc-form-login" }),
                  let action = try attribute("action", from: form), let actionURL = URL(string: decodeHTML(action), relativeTo: url)?.absoluteURL,
                  sameOrigin(actionURL, root) else { throw Failure.rejected }
            var fields = ["username": credentials.username, "password": credentials.password, "credentialId": ""]
            for input in try matches("<input\\b[^>]*>", in: html) {
                if try attribute("type", from: input)?.lowercased() == "hidden", let name = try attribute("name", from: input),
                   !["username", "password"].contains(name) { fields[name] = decodeHTML(try attribute("value", from: input) ?? "") }
            }
            var encoded = URLComponents(); encoded.queryItems = fields.sorted { $0.key < $1.key }.map { .init(name: $0.key, value: $0.value) }
            request = URLRequest(url: actionURL); request.httpMethod = "POST"
            request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
            request.setValue(root.absoluteString, forHTTPHeaderField: "Origin")
            request.setValue(url.absoluteString, forHTTPHeaderField: "Referer")
            request.httpBody = Data((encoded.percentEncodedQuery ?? "").replacingOccurrences(of: "+", with: "%2B").utf8)
            submitted = true
        }
        throw Failure.rejected
    }
    private func sameOrigin(_ left: URL, _ right: URL) -> Bool {
        left.scheme == "https" && left.scheme == right.scheme && left.host == right.host && (left.port ?? 443) == (right.port ?? 443) && left.user == nil && left.password == nil
    }
    private func matches(_ pattern: String, in text: String) throws -> [String] {
        let regex = try NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
        return regex.matches(in: text, range: NSRange(text.startIndex..., in: text)).compactMap { Range($0.range, in: text).map { String(text[$0]) } }
    }
    private func attribute(_ key: String, from tag: String) throws -> String? {
        let regex = try NSRegularExpression(pattern: "\\b" + NSRegularExpression.escapedPattern(for: key) + "\\s*=\\s*([\"'])(.*?)\\1", options: [.caseInsensitive, .dotMatchesLineSeparators])
        guard let match = regex.firstMatch(in: tag, range: NSRange(tag.startIndex..., in: tag)), let range = Range(match.range(at: 2), in: tag) else { return nil }
        return String(tag[range])
    }
    private func decodeHTML(_ text: String) -> String {
        text.replacingOccurrences(of: "&amp;", with: "&").replacingOccurrences(of: "&quot;", with: "\"")
            .replacingOccurrences(of: "&#39;", with: "'").replacingOccurrences(of: "&lt;", with: "<").replacingOccurrences(of: "&gt;", with: ">")
    }
}
