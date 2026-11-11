// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import CryptoKit

/** Durable envelope for the locally persisted native snapshot. `ack` and `localInputEpoch`
 *  are the resume fences these exact bytes were validated at, so they can never diverge
 *  from the snapshot contents. Restoring them today is display-only: the host snapshot
 *  stays authoritative because the host may hold a younger or rewound event sequence
 *  than this device. */
struct PersistedTerminalSnapshot: Codable {
    var version: Int
    var ack: UInt64
    var localInputEpoch: UInt64
    var snapshot: Data
    static func storageName(_ session: UUID) -> String { "terminal:" + session.uuidString }
}

/// SnapshotCompressionV1 is a protobuf enum: zero is UNSPECIFIED, NONE is 1.
/// Accepting a different value would change the authenticated container contract.
enum TerminalSnapshotProtocol {
    static func validateCompression(_ compression: UInt64) throws {
        guard compression == 1 else { throw ClientError.protocolViolation("snapshot compression") }
    }
    static func validate(_ container: ProtoMessage, sessionID: UUID, pcDeviceID: UUID,
                         recipientID: UUID, keyEpoch: Int64, snapshotSequence: UInt64,
                         minimumSequence: UInt64) throws {
        try container.validateSingular(Set(1...17))
        guard keyEpoch > 0, container.uint(1)==2,container.bytes(2)==sessionID.bytes,
              container.bytes(3)==pcDeviceID.bytes,container.bytes(4)==recipientID.bytes,
              container.uint(5)==UInt64(keyEpoch),container.uint(13)<=64*1024*1024,
              container.uint(7)==snapshotSequence,container.uint(7)>0,container.uint(7)<=UInt64(Int64.max),
              container.uint(7)>=minimumSequence,container.uint(8)>0,container.uint(8)<=512,
              container.uint(9)>0,container.uint(9)<=512,
              try container.text(10)=="TERMLINK_TERMINAL_CORE_SNAPSHOT",container.uint(11)==2,
              container.bytes(14).count==12 else { throw ClientError.protocolViolation("snapshot identity") }
        try validateCompression(container.uint(12))
        guard try container.text(6)==keyID(recipient:recipientID,epoch:keyEpoch) else {
            throw ClientError.protocolViolation("snapshot key")
        }
    }
    static func open(_ container: ProtoMessage, pairwise: SymmetricKey, sessionID: UUID,
                     pcDeviceID: UUID, recipientID: UUID, keyEpoch: Int64,
                     snapshotSequence: UInt64, minimumSequence: UInt64) throws -> Data {
        try validate(container,sessionID:sessionID,pcDeviceID:pcDeviceID,recipientID:recipientID,
                     keyEpoch:keyEpoch,snapshotSequence:snapshotSequence,minimumSequence:minimumSequence)
        let key=deriveKey(pairwise,sessionID:sessionID,recipientID:recipientID,keyEpoch:keyEpoch)
        let plain=try SessionCrypto.open(container.bytes(15),key:key,nonce:container.bytes(14),aad:associatedData(container))
        guard plain.count==Int(container.uint(13)) else{throw ClientError.protocolViolation("snapshot size")}
        return plain
    }
    static func deriveKey(_ pairwise: SymmetricKey, sessionID: UUID, recipientID: UUID, keyEpoch: Int64) -> SymmetricKey {
        let id=Data(keyID(recipient:recipientID,epoch:keyEpoch).utf8)
        var length=UInt16(id.count).bigEndian
        let info=Data("TERMLINK-TERMINAL-CORE-SNAPSHOT-KEY-V2\0".utf8)+recipientID.bytes + Data.bigEndian(UInt64(keyEpoch)) + withUnsafeBytes(of:&length){Data($0)} + id
        // Snapshot objects have a session-UUID salt and a length-prefixed key ID;
        // ordinary transport/file envelopes deliberately use a different KDF.
        return HKDF<SHA256>.deriveKey(inputKeyMaterial:pairwise,salt:sessionID.bytes,info:info,outputByteCount:32)
    }
    static func associatedData(_ container: ProtoMessage) -> Data {
        var aad=Data("TERMLINK-TERMINAL-CORE-SNAPSHOT-OBJECT-V2\0".utf8)
        func u32(_ v:UInt64){var n=UInt32(v).bigEndian;aad.append(withUnsafeBytes(of:&n){Data($0)})}
        func string(_ d:Data){var n=UInt16(d.count).bigEndian;aad.append(withUnsafeBytes(of:&n){Data($0)});aad.append(d)}
        u32(container.uint(1));aad.append(container.bytes(2));aad.append(container.bytes(3));aad.append(container.bytes(4));aad.append(.bigEndian(container.uint(5)));string(container.bytes(6));aad.append(.bigEndian(container.uint(7)));u32(container.uint(8));u32(container.uint(9));string(container.bytes(10));u32(container.uint(11));u32(container.uint(12));aad.append(.bigEndian(container.uint(13)));aad.append(.bigEndian(container.uint(16)));aad.append(.bigEndian(container.uint(17)));aad.append(container.bytes(14))
        return aad
    }
    private static func keyID(recipient: UUID, epoch: Int64) -> String {
        let recipient=recipient.uuidString.replacingOccurrences(of:"-",with:"").lowercased()
        return String(format:"terminal-core-snapshot-v2:%@:%08lld",recipient,epoch)
    }
}
