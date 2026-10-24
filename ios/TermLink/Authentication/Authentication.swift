// SPDX-License-Identifier: GPL-3.0-only
import AuthenticationServices
import CryptoKit
import Foundation
import UIKit

struct ClientConfiguration: Codable, Sendable {
    let issuer: URL, clientID: String, redirect: URL
    static func load(root: URL) async throws -> Self {
        let json = try await APIClient.publicJSON(root.appendingPathComponent("v1/client-config"))
        guard let issuer = URL(string: try json.required("oidc_issuer")), issuer.scheme == "https", issuer.user == nil,
              let redirect = URL(string: try json.required("ios_redirect_uri")), redirect.scheme == "dev.termlink.ios", redirect.absoluteString == "dev.termlink.ios:/oauth2redirect" else {
            throw ClientError.unavailable("服务端尚未配置 iOS 登录，请先启用 iOS 客户端身份。")
        }
        return Self(issuer: issuer, clientID: try json.required("ios_oidc_client_id"), redirect: redirect)
    }
    func discovery() async throws -> JSONValue {
        let value = try await APIClient.publicJSON(issuer.appendingPathComponent(".well-known/openid-configuration"))
        guard value["issuer"].string.trimmingCharacters(in: CharacterSet(charactersIn: "/")) == issuer.absoluteString.trimmingCharacters(in: CharacterSet(charactersIn: "/")) else { throw ClientError.authentication }
        for field in ["authorization_endpoint", "token_endpoint"] {
            guard let u = URL(string: value[field].string), u.scheme == "https", u.user == nil, u.password == nil, u.host == issuer.host else { throw ClientError.invalidResponse(field) }
        }
        return value
    }
}

struct TokenSet: Codable, Sendable {
    var access: String, refresh: String
    var expires: Date
    let configuration: ClientConfiguration
}
actor TokenStore {
    private let account: String
    private var value: TokenSet?
    private var refreshTask: Task<TokenSet, Error>?
    private var generation = UUID()
    init(scope: String) throws {
        account = "tokens:" + Data(SHA256.hash(data: Data(scope.utf8))).hex
        if let data = try KeychainStore().read(account) { value = try JSONDecoder().decode(TokenSet.self, from: data) }
    }
    func save(_ tokens: TokenSet) throws {
        try KeychainStore().write(JSONEncoder().encode(tokens), account: account); value = tokens
    }
    func clear() throws { generation = UUID(); refreshTask?.cancel(); refreshTask = nil; value = nil; try KeychainStore().remove(account) }
    func isSignedIn() -> Bool { value != nil }
    func identityScope() async throws -> String {
        guard let current = value else { throw ClientError.authentication }
        let token = try await accessToken(), parts = token.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { throw ClientError.authentication }
        let claims = try JSONValue.decode(Data(base64URL: String(parts[1])), maximum: 65536)
        let subject = try claims.required("sub")
        guard subject.utf8.count <= 512 else { throw ClientError.limitExceeded }
        // This value partitions local storage only. The service verifies the token and all access rights.
        return current.configuration.issuer.absoluteString + "\n" + current.configuration.clientID + "\n" + subject
    }
    func accessToken() async throws -> String {
        guard let v = value else { throw ClientError.authentication }
        if v.expires.timeIntervalSinceNow < 45 { try await refresh(rejected: v.access) }
        guard let current = value else { throw ClientError.authentication }; return current.access
    }
    func refresh(rejected: String) async throws {
        guard let current = value else { throw ClientError.authentication }
        if current.access != rejected && current.expires.timeIntervalSinceNow > 45 { return }
        let owner = generation
        let task: Task<TokenSet, Error>
        if let existing = refreshTask { task = existing }
        else {
            task = Task {
                let metadata = try await current.configuration.discovery()
                return try await Self.exchange(endpoint: URL(string: metadata["token_endpoint"].string)!, configuration: current.configuration,
                    fields: ["grant_type":"refresh_token", "refresh_token":current.refresh, "client_id":current.configuration.clientID], oldRefresh: current.refresh)
            }
            refreshTask = task
        }
        do { let next = try await task.value; guard owner == generation else { throw CancellationError() }; try save(next); refreshTask = nil }
        catch { if owner == generation { refreshTask = nil }; throw error }
    }
    static func exchange(endpoint: URL, configuration: ClientConfiguration, fields: [String:String], oldRefresh: String = "") async throws -> TokenSet {
        var form = URLComponents(); form.queryItems = fields.sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
        var request = URLRequest(url: endpoint); request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.httpBody = Data((form.percentEncodedQuery ?? "").replacingOccurrences(of: "+", with: "%2B").utf8)
        let delegate = NoRedirectDelegate(), session = URLSession(configuration: .ephemeral, delegate: delegate, delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        let (bytes,response) = try await session.bytes(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw ClientError.authentication }
        var data = Data(); for try await b in bytes { guard data.count < 65536 else { throw ClientError.limitExceeded }; data.append(b) }
        let result = try JSONValue.decode(data), access = try result.required("access_token")
        guard result["token_type"].string.lowercased() == "bearer", result["expires_in"].integer > 0 else { throw ClientError.authentication }
        let refresh = result["refresh_token"].string.isEmpty ? oldRefresh : result["refresh_token"].string
        guard !refresh.isEmpty else { throw ClientError.authentication }
        return .init(access: access, refresh: refresh, expires: Date().addingTimeInterval(TimeInterval(result["expires_in"].integer)), configuration: configuration)
    }
}

@MainActor final class AuthenticationCoordinator: NSObject, ASWebAuthenticationPresentationContextProviding {
    private var session: ASWebAuthenticationSession?
    private var completion: CheckedContinuation<URL, Error>?
    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.flatMap(\.windows).first { $0.isKeyWindow } ?? ASPresentationAnchor()
    }
    func signIn(root: URL) async throws -> TokenSet {
        guard session == nil else { throw ClientError.unavailable("已有登录窗口打开。") }
        let config = try await ClientConfiguration.load(root: root), discovery = try await config.discovery()
        let verifier = try SessionCrypto.random(32).base64URL, state = try SessionCrypto.random(32).base64URL
        var url = URLComponents(string: try discovery.required("authorization_endpoint"))!
        url.queryItems = [.init(name:"response_type",value:"code"),.init(name:"client_id",value:config.clientID),.init(name:"redirect_uri",value:config.redirect.absoluteString),
            .init(name:"scope",value:"openid profile email offline_access"),.init(name:"state",value:state),.init(name:"code_challenge",value:Data(SHA256.hash(data:Data(verifier.utf8))).base64URL),.init(name:"code_challenge_method",value:"S256")]
        let callback: URL = try await withTaskCancellationHandler(operation: {
            try await withCheckedThrowingContinuation { continuation in
                completion = continuation
                let web = ASWebAuthenticationSession(url: url.url!, callbackURLScheme: config.redirect.scheme) { [weak self] result, error in
                    Task { @MainActor in
                        guard let self, let completion = self.completion else { return }; self.completion = nil; self.session = nil
                        if let result { completion.resume(returning:result) } else { completion.resume(throwing:error ?? ClientError.cancelled) }
                    }
                }
                session = web; web.presentationContextProvider = self
                if !web.start() { completion = nil; session = nil; continuation.resume(throwing:ClientError.unavailable("无法打开登录窗口。")) }
            }
        }, onCancel: { Task { @MainActor in self.cancel() } })
        let values = URLComponents(url: callback, resolvingAgainstBaseURL: false)?.queryItems ?? []
        guard callback.scheme == config.redirect.scheme, callback.host == config.redirect.host, callback.path == config.redirect.path,
              values.filter({ $0.name == "state" }).count == 1, values.first(where: { $0.name == "state" })?.value == state,
              values.filter({ $0.name == "code" }).count == 1, let code = values.first(where: { $0.name == "code" })?.value, !code.isEmpty else { throw ClientError.authentication }
        return try await TokenStore.exchange(endpoint: URL(string: discovery["token_endpoint"].string)!, configuration: config,
            fields:["grant_type":"authorization_code","code":code,"client_id":config.clientID,"redirect_uri":config.redirect.absoluteString,"code_verifier":verifier])
    }
    func cancel() { session?.cancel(); session = nil; completion?.resume(throwing:CancellationError()); completion = nil }
}
