// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import Foundation
import Security

struct KeychainStore {
    let service = "dev.termlink.ios"
    func read(_ account: String) throws -> Data? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
            kSecAttrAccount as String: account, kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = item as? Data else { throw ClientError.unavailable("钥匙串暂不可用。") }
        return data
    }
    func write(_ data: Data, account: String) throws {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
        let attributes: [String: Any] = [kSecValueData as String: data, kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly]
        let status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            guard SecItemAdd(query.merging(attributes) { _, new in new } as CFDictionary, nil) == errSecSuccess else { throw ClientError.unavailable("无法安全保存凭据。") }
        } else if status != errSecSuccess { throw ClientError.unavailable("无法安全更新凭据。") }
    }
    func remove(_ account: String) throws {
        let status = SecItemDelete([kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account] as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw ClientError.unavailable("无法清理凭据。") }
    }
}

struct DeviceIdentity: Codable, Sendable {
    let id: UUID
    private let signing: Data
    private let exchange: Data
    init() { id = UUID(); signing = Curve25519.Signing.PrivateKey().rawRepresentation; exchange = Curve25519.KeyAgreement.PrivateKey().rawRepresentation }
    var signingKey: Curve25519.Signing.PrivateKey { get throws { try .init(rawRepresentation: signing) } }
    var exchangeKey: Curve25519.KeyAgreement.PrivateKey { get throws { try .init(rawRepresentation: exchange) } }
    var publicSigning: Data { get throws { try signingKey.publicKey.rawRepresentation } }
    var publicExchange: Data { get throws { try exchangeKey.publicKey.rawRepresentation } }
    func sign(_ data: Data) throws -> Data { try signingKey.signature(for: data) }
    static func load(scope: String, store: KeychainStore = .init()) throws -> Self {
        let account = "identity:" + Data(SHA256.hash(data: Data(scope.utf8))).hex
        if let data = try store.read(account) { return try JSONDecoder().decode(Self.self, from: data) }
        let value = Self(); try store.write(JSONEncoder().encode(value), account: account); return value
    }
}

struct SecureState {
    let scope: String
    private var account: String { "state:" + Data(SHA256.hash(data: Data(scope.utf8))).hex }
    private var key: SymmetricKey {
        get throws {
            let store = KeychainStore()
            if let data = try store.read(account) { guard data.count == 32 else { throw ClientError.authentication }; return SymmetricKey(data: data) }
            let key = SymmetricKey(size: .bits256)
            try store.write(key.withUnsafeBytes { Data($0) }, account: account)
            return key
        }
    }
    private var directory: URL { get throws {
        let d = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true).appendingPathComponent("PrivateState", isDirectory: true)
        try FileManager.default.createDirectory(at: d, withIntermediateDirectories: true)
        var mutable = d; var values = URLResourceValues(); values.isExcludedFromBackup = true; try mutable.setResourceValues(values)
        return d
    } }
    private func url(_ name: String) throws -> URL { try directory.appendingPathComponent(Data(SHA256.hash(data: Data((account + ":" + name).utf8))).hex) }
    func load<T: Decodable>(_ name: String, as type: T.Type) throws -> T? {
        let location = try url(name)
        guard FileManager.default.fileExists(atPath: location.path) else { return nil }
        let data = try Data(contentsOf: location)
        guard data.count <= 64 * 1024 * 1024 else { throw ClientError.limitExceeded }
        let plain = try AES.GCM.open(.init(combined: data), using: key, authenticating: Data((scope + ":" + name).utf8))
        return try JSONDecoder().decode(type, from: plain)
    }
    func save<T: Encodable>(_ value: T, name: String) throws {
        let data = try JSONEncoder().encode(value)
        guard data.count <= 64 * 1024 * 1024 else { throw ClientError.limitExceeded }
        guard let sealed = try AES.GCM.seal(data, using: key, authenticating: Data((scope + ":" + name).utf8)).combined else { throw ClientError.invalidResponse("encrypted state") }
        try sealed.write(to: url(name), options: [.atomic, .completeFileProtection])
    }
}
