// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import Foundation

struct APIError: LocalizedError, Sendable {
    let status: Int, code: String, traceID: String
    var errorDescription: String? { "HTTP \(status) · \(code)" + (traceID.isEmpty ? "" : " · \(traceID)") }
}
final class NoRedirectDelegate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}
actor APIClient {
    let root: URL
    let identity: DeviceIdentity
    let tokens: TokenStore
    private let delegate = NoRedirectDelegate()
    private lazy var session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil; config.urlCache = nil; config.timeoutIntervalForRequest = 30
        return URLSession(configuration: config, delegate: delegate, delegateQueue: nil)
    }()
    init(root: URL, identity: DeviceIdentity, tokens: TokenStore) { self.root = root; self.identity = identity; self.tokens = tokens }
    static func origin(_ value: String) throws -> URL {
        guard var c = URLComponents(string: value.trimmingCharacters(in: .whitespacesAndNewlines)), c.scheme == "https", c.host != nil,
              c.user == nil, c.password == nil, c.query == nil, c.fragment == nil, c.path.isEmpty || c.path == "/" else { throw ClientError.invalidURL }
        c.path = ""; guard let url = c.url else { throw ClientError.invalidURL }; return url
    }
    static func publicJSON(_ url: URL) async throws -> JSONValue {
        guard url.scheme == "https", url.user == nil, url.password == nil else { throw ClientError.invalidURL }
        let delegate = NoRedirectDelegate(), session = URLSession(configuration: .ephemeral, delegate: delegate, delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        let (bytes, response) = try await session.bytes(from: url)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else { throw ClientError.unavailable("无法读取客户端配置。") }
        var data = Data(); for try await byte in bytes { guard data.count < 65536 else { throw ClientError.limitExceeded }; data.append(byte) }
        return try .decode(data, maximum: 65536)
    }
    func signedRequest(path: String, method: String, body: Data = Data(), idempotency: UUID? = nil, proof: Bool = true, token: String? = nil) async throws -> URLRequest {
        guard path.hasPrefix("/"), !path.hasPrefix("//"), !path.contains("#"), let url = URL(string: root.absoluteString + path), url.host == root.host else { throw ClientError.invalidURL }
        var request = URLRequest(url: url); request.httpMethod = method
        request.setValue("Bearer \(try await tokenValue(token))", forHTTPHeaderField: "Authorization")
        if method != "GET" { request.httpBody = body; request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        if let idempotency { request.setValue(idempotency.uuidString.lowercased(), forHTTPHeaderField: "Idempotency-Key") }
        if proof {
            let timestamp = String(Int64(Date().timeIntervalSince1970 * 1000)), nonce = try SessionCrypto.random(16).base64URL
            let canonical = ["TERMLINK-DEVICE-PROOF-V1",method,path,timestamp,nonce,Data(SHA256.hash(data: body)).hex].joined(separator: "\n")
            request.setValue(identity.id.uuidString.lowercased(), forHTTPHeaderField: "X-TermLink-Device")
            request.setValue(timestamp, forHTTPHeaderField: "X-TermLink-Timestamp")
            request.setValue(nonce, forHTTPHeaderField: "X-TermLink-Nonce")
            request.setValue(try identity.sign(Data(canonical.utf8)).base64URL, forHTTPHeaderField: "X-TermLink-Signature")
        }
        if path == "/v1/pcs" { request.setValue("powershell-7-enhanced", forHTTPHeaderField: "X-TermLink-PC-Command-Capabilities") }
        return request
    }
    private func tokenValue(_ value: String?) async throws -> String { if let value { return value }; return try await tokens.accessToken() }
    func request(_ path: String, method: String = "GET", body: JSONValue = .null, idempotency: UUID? = nil, proof: Bool = true) async throws -> JSONValue {
        let data = body == .null ? Data() : try body.data()
        for attempt in 0..<2 {
            let token = try await tokens.accessToken()
            let request = try await signedRequest(path: path, method: method, body: data, idempotency: idempotency, proof: proof, token: token)
            let (bytes, response) = try await session.bytes(for: request)
            guard let http = response as? HTTPURLResponse else { throw ClientError.invalidResponse("HTTP") }
            var raw = Data(); for try await byte in bytes { guard raw.count < 1_048_576 else { throw ClientError.limitExceeded }; raw.append(byte) }
            if http.statusCode == 401 && attempt == 0 { try await tokens.refresh(rejected: token); continue }
            let value = raw.isEmpty ? JSONValue.null : (try? JSONValue.decode(raw)) ?? .null
            guard (200..<300).contains(http.statusCode) else { throw APIError(status: http.statusCode, code: value["error"]["code"].string.isEmpty ? "HTTP_\(http.statusCode)" : value["error"]["code"].string, traceID: value["error"]["trace_id"].string) }
            if !raw.isEmpty && value == .null { throw ClientError.invalidResponse("JSON") }
            return value
        }
        throw ClientError.authentication
    }
    func register(name: String) async throws -> JSONValue {
        let type = "IOS", id = identity.id.uuidString.lowercased(), signing = try identity.publicSigning.base64URL, exchange = try identity.publicExchange.base64URL
        let keyID = "device-key-\(id)", timestamp = Int64(Date().timeIntervalSince1970 * 1000), nonce = try SessionCrypto.random(16).base64URL
        let canonical = ["TERMLINK-DEVICE-REGISTER-V1",id,type,name,signing,exchange,keyID,String(timestamp),nonce].joined(separator: "\n")
        return try await request("/v1/devices/register", method: "POST", body: .object([
            "id": .string(id), "type": .string(type), "name": .string(name), "signing_public_key": .string(signing), "exchange_public_key": .string(exchange),
            "key_id": .string(keyID), "timestamp_ms": .number(Double(timestamp)), "nonce": .string(nonce), "signature": .string(try identity.sign(Data(canonical.utf8)).base64URL)
        ]), proof: false)
    }
}
