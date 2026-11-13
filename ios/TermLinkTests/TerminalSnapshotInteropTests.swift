// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import XCTest
@testable import TermLink

final class TerminalSnapshotInteropTests: XCTestCase {
    private func fixture() throws -> JSONValue {
        let url = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "mac-native-snapshot-v2", withExtension: "json"))
        return try JSONValue.decode(Data(contentsOf: url))
    }
    private func hex(_ text: String) throws -> Data {
        guard text.count % 2 == 0 else { throw ClientError.invalidResponse("fixture hex") }
        var bytes=Data(),index=text.startIndex
        while index<text.endIndex { let end=text.index(index,offsetBy:2);guard let byte=UInt8(text[index..<end],radix:16) else{throw ClientError.invalidResponse("fixture hex")};bytes.append(byte);index=end }
        return bytes
    }
    private func open(_ container: ProtoMessage, fixture f: JSONValue) throws -> Data {
        try TerminalSnapshotProtocol.open(container,pairwise:SymmetricKey(data:hex(f["pairwise_key"].string)),
            sessionID:XCTUnwrap(UUID(uuidString:f["session_id"].string)),
            pcDeviceID:XCTUnwrap(UUID(uuidString:f["pc_device_id"].string)),
            recipientID:XCTUnwrap(UUID(uuidString:f["mobile_device_id"].string)),
            keyEpoch:f["key_epoch"].integer,snapshotSequence:UInt64(f["snapshot_sequence"].integer),minimumSequence:0)
    }
    func testActualMacSealedChunksDecryptAndImportIntoRealIOSNativeCore() throws {
        let f=try fixture();XCTAssertTrue(f["public_fixture_only"].bool)
        var sealed=Data()
        for (index,chunk) in f["chunks"].array.enumerated() {
            let p=try ProtoMessage(hex(chunk["response_hex"].string))
            XCTAssertEqual(p.uint(2),1);XCTAssertEqual(p.uint(3),1)
            XCTAssertEqual(p.uint(22),UInt64(index));XCTAssertEqual(p.uint(23),UInt64(f["chunks"].array.count))
            XCTAssertEqual(p.uint(5),UInt64(f["snapshot_sequence"].integer))
            sealed.append(p.bytes(24))
            XCTAssertEqual(p.bytes(20),try hex(f["sealed_object_sha256"].string))
        }
        XCTAssertEqual(Data(SHA256.hash(data:sealed)),try hex(f["sealed_object_sha256"].string))
        let container=try ProtoMessage(sealed)
        XCTAssertEqual(container.uint(12),1)
        XCTAssertEqual(TerminalSnapshotProtocol.associatedData(container),try hex(f["associated_data"].string))
        let key=try TerminalSnapshotProtocol.deriveKey(SymmetricKey(data:hex(f["pairwise_key"].string)),
            sessionID:XCTUnwrap(UUID(uuidString:f["session_id"].string)),recipientID:XCTUnwrap(UUID(uuidString:f["mobile_device_id"].string)),keyEpoch:f["key_epoch"].integer)
        XCTAssertEqual(key.withUnsafeBytes{Data($0)},try hex(f["key"].string))
        let plain=try open(container,fixture:f)
        XCTAssertEqual(Data(SHA256.hash(data:plain)),try hex(f["checkpoint_sha256"].string))
        let core=try TLTerminalBridge(columns:80,rows:24,sequence:1)
        try core.importSnapshot(plain)
        let frame=try TerminalFrame.decode(core.renderFrame())
        XCTAssertEqual(frame.nextSequence,UInt64(f["next_source_sequence"].integer))
        XCTAssertEqual(frame.columns,80);XCTAssertEqual(frame.rows,24)
        XCTAssertTrue(frame.cells.map(\.text).joined().contains(f["plaintext_marker"].string))
    }
    func testActualMacMultiChunkSnapshotAndFollowingOutputReachSameNativeCheckpoint() throws {
        let bundle=Bundle(for:Self.self),prefix="mac-native-snapshot-v2-multichunk"
        let metadata=try XCTUnwrap(bundle.url(forResource:prefix,withExtension:"json"))
        let f=try JSONValue.decode(Data(contentsOf:metadata))
        XCTAssertEqual(f["chunks"].array.count,2)
        var sealed=Data()
        for (index,_) in f["chunks"].array.enumerated() {
            let url=try XCTUnwrap(bundle.url(forResource:prefix+"-chunk"+String(index),withExtension:"bin"))
            let chunk=try ProtoMessage(Data(contentsOf:url))
            XCTAssertEqual(chunk.uint(22),UInt64(index));XCTAssertEqual(chunk.uint(23),2)
            XCTAssertEqual(chunk.uint(5),UInt64(f["snapshot_sequence"].integer))
            XCTAssertEqual(chunk.bytes(20),try hex(f["sealed_object_sha256"].string))
            sealed.append(chunk.bytes(24))
        }
        XCTAssertEqual(sealed.count,Int(f["sealed_object_bytes"].integer))
        XCTAssertEqual(Data(SHA256.hash(data:sealed)),try hex(f["sealed_object_sha256"].string))
        let container=try ProtoMessage(sealed,maximum:64*1024*1024)
        let plain=try open(container,fixture:f)
        XCTAssertEqual(Data(SHA256.hash(data:plain)),try hex(f["checkpoint_sha256"].string))
        let core=try TLTerminalBridge(columns:80,rows:24,sequence:1)
        try core.importSnapshot(plain)
        XCTAssertEqual(try TerminalFrame.decode(core.renderFrame()).nextSequence,UInt64(f["next_source_sequence"].integer))
        for event in f["replay"].array {
            let payload=try ProtoMessage(hex(event["payload_hex"].string))
            XCTAssertEqual(event["message_type"].integer,20)
            try core.applyData(payload.bytes(1),kind:1,sequence:UInt64(event["sequence"].integer),columns:0,rows:0)
        }
        XCTAssertEqual(try TerminalFrame.decode(core.renderFrame()).nextSequence,UInt64(f["replay_next_source_sequence"].integer))
        XCTAssertEqual(Data(SHA256.hash(data:try core.exportSnapshot())),try hex(f["replay_checkpoint_sha256"].string))
    }
    func testMutatedCompressionRecipientAndCiphertextAreRejected() throws {
        let f=try fixture(),original=try ProtoMessage(hex(f["sealed_object"].string))
        for value: UInt64 in [0,2] {var changed=original;changed.set(12,value);XCTAssertThrowsError(try open(changed,fixture:f))}
        var recipient=original;recipient.set(4,UUID().bytes);XCTAssertThrowsError(try open(recipient,fixture:f))
        var ciphertext=original,bytes=original.bytes(15);bytes[0]^=1;ciphertext.set(15,bytes);XCTAssertThrowsError(try open(ciphertext,fixture:f))
        var duplicate=original;duplicate.add(6,original.bytes(6));XCTAssertThrowsError(try open(duplicate,fixture:f))
    }
}
