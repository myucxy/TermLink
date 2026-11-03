// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import Foundation
import Security

enum SessionCrypto {
    static let salt = Data(SHA256.hash(data: Data("TERMLINK-E2EE-V1".utf8)))
    static func derive(_ input: SymmetricKey, info: Data) -> SymmetricKey { HKDF<SHA256>.deriveKey(inputKeyMaterial: input, salt: salt, info: info, outputByteCount: 32) }
    static func pairwise(identity: DeviceIdentity, peer: Data, pc: UUID, mobile: UUID, epoch: Int64) throws -> SymmetricKey {
        guard peer.count == 32, epoch > 0 else { throw ClientError.protocolViolation("peer key epoch") }
        let secret = try identity.exchangeKey.sharedSecretFromKeyAgreement(with: .init(rawRepresentation: peer))
        var info = Data("TERMLINK-PAIRWISE-V1\0".utf8)
        info.append(pc.bytes)
        info.append(mobile.bytes)
        info.append(Data.bigEndian(UInt64(epoch)))
        return secret.hkdfDerivedSymmetricKey(using: SHA256.self, salt: salt,
            sharedInfo: info, outputByteCount: 32)
    }
    static func session(_ pairwise: SymmetricKey, id: UUID, direction: UInt8, keyID: Data) -> SymmetricKey {
        derive(pairwise, info: Data("TERMLINK-SESSION-KEY-V1\0".utf8) + id.bytes + Data([direction]) + keyID)
    }
    static func history(_ pairwise: SymmetricKey, id: UUID, recipient: UUID, epoch: UInt64, keyID: Data) -> SymmetricKey {
        derive(pairwise, info: Data("TERMLINK-HISTORY-KEY-V1\0".utf8) + id.bytes + recipient.bytes + Data.bigEndian(epoch) + keyID)
    }
    static func seal(_ plain: Data, key: SymmetricKey, nonce: Data, aad: Data) throws -> Data {
        let box = try ChaChaPoly.seal(plain, using: key, nonce: .init(data: nonce), authenticating: aad)
        return box.ciphertext + box.tag
    }
    static func open(_ cipher: Data, key: SymmetricKey, nonce: Data, aad: Data) throws -> Data {
        guard cipher.count >= 16 else { throw ClientError.protocolViolation("AEAD tag") }
        return try ChaChaPoly.open(.init(nonce: .init(data: nonce), ciphertext: cipher.dropLast(16), tag: cipher.suffix(16)), using: key, authenticating: aad)
    }
    static func random(_ count: Int) throws -> Data {
        var bytes = [UInt8](repeating: 0, count: count)
        guard SecRandomCopyBytes(kSecRandomDefault, count, &bytes) == errSecSuccess else { throw ClientError.unavailable("安全随机数不可用。") }
        return Data(bytes)
    }
    static func envelope(id: UUID, sender: UUID, recipient: UUID, type: WireType, sequence: UInt64, ack: UInt64, keyID: Data, key: SymmetricKey, payload: Data) throws -> ProtoMessage {
        guard sequence > 0, keyID.count <= 128 else { throw ClientError.protocolViolation("envelope sequence") }
        var m = ProtoMessage(); m.set(1, UInt64(1)); m.set(2, type.rawValue); m.set(3, id.bytes); m.set(4, sender.bytes)
        m.set(5, UInt64(2)); m.set(6, sequence); m.set(7, ack); m.set(8, UInt64(Date().timeIntervalSince1970 * 1000))
        m.set(9, keyID); m.set(10, try random(12)); m.set(12, recipient.bytes)
        m.set(11, try seal(payload, key: key, nonce: m.bytes(10), aad: m.encoded()))
        return m
    }
}
