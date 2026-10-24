// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import XCTest
import UIKit
@testable import TermLink

final class ProtocolTests: XCTestCase {
    private func hex(_ value: String) throws -> Data {
        guard value.count % 2 == 0 else { throw ClientError.invalidResponse("test hex") }
        var result = Data(); var i = value.startIndex
        while i < value.endIndex { let end = value.index(i, offsetBy: 2); guard let byte = UInt8(value[i..<end], radix: 16) else { throw ClientError.invalidResponse("test hex") }; result.append(byte); i = end }
        return result
    }
    func testSessionEncryptionMatchesSharedAndroidWindowsVector() throws {
        let pc = UUID(uuidString: "00112233-4455-6677-8899-aabbccddeeff")!
        let mobile = UUID(uuidString: "10213243-5465-7687-98a9-bacbdcedfe0f")!
        let session = UUID(uuidString: "fedcba98-7654-3210-ffff-eeeeeeeeeeee")!
        let seed = try hex("202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f")
        let encoded = try JSONValue.object(["id": .string(mobile.uuidString), "signing": .string(seed.base64EncodedString()), "exchange": .string(seed.base64EncodedString())]).data()
        let identity = try JSONDecoder().decode(DeviceIdentity.self, from: encoded)
        let pair = try SessionCrypto.pairwise(identity: identity, peer: hex("8f40c5adb68f25624ae5b214ea767a6ec94d829d3d7b5e1ad1ba6f3e2138285f"), pc: pc, mobile: mobile, epoch: 7)
        XCTAssertEqual(pair.withUnsafeBytes { Data($0).hex }, "b1632a5a77cafec55f885fc4c3588f303ed3ca9856d5c464636e0b5feaefa773")
        let key = SessionCrypto.session(pair, id: session, direction: 1, keyID: Data("epoch-00000007".utf8))
        XCTAssertEqual(key.withUnsafeBytes { Data($0).hex }, "e95cacb46c64bb90b7a7311b3ae253add4e29dddfb2ded34d576cb866da7695c")
        let aad = try hex("080110141a10fedcba9876543210ffffeeeeeeeeeeee221000112233445566778899aabbccddeeff2801302a380740fba0f1c2b1344a0e65706f63682d3030303030303037520caabbccdd000000000000002a")
        let plain = try hex("1b5b33316d5465726d4c696e6b1b5b306d0d0a"), nonce = try hex("aabbccdd000000000000002a")
        let cipher = try SessionCrypto.seal(plain, key: key, nonce: nonce, aad: aad)
        XCTAssertEqual(cipher.hex, "6a97c75f0d8c984923467129d973a325134d69c9bd792587797651196a7498a68e3c44")
        XCTAssertEqual(try SessionCrypto.open(cipher, key: key, nonce: nonce, aad: aad), plain)
        var altered = aad; altered[0] ^= 1
        XCTAssertThrowsError(try SessionCrypto.open(cipher, key: key, nonce: nonce, aad: altered))
        XCTAssertEqual(try ProtoMessage(aad).encoded(), aad)
    }
    func testProtobufRejectsOverflowDuplicateAndTruncatedFields() throws {
        XCTAssertThrowsError(try ProtoMessage(Data([8] + Array(repeating: 255, count: 9) + [2])))
        XCTAssertThrowsError(try ProtoMessage(Data([18, 3, 1])))
        XCTAssertThrowsError(try ProtoMessage(Data([0])))
        let duplicate = try ProtoMessage(Data([8, 1, 8, 2]))
        XCTAssertThrowsError(try duplicate.validateSingular([1]))
    }
    func testServerOriginRejectsCredentialsAndPath() throws {
        XCTAssertEqual(try APIClient.origin("https://example.test:8910/").absoluteString, "https://example.test:8910")
        for input in ["http://example.test", "https://user:secret@example.test", "https://example.test/path", "https://example.test/?token=x", "https://example.test/#fragment"] {
            XCTAssertThrowsError(try APIClient.origin(input))
        }
    }
    @MainActor func testAgentAliasesKeepDedicatedAssetsAndUnknownFallback() {
        for alias in ["Codex", "Claude Code", "claude-code", "open-code", "Open Code", "Grok Build", "ZCode"] {
            XCTAssertNotNil(AgentBrand.asset(alias))
            if let asset=AgentBrand.asset(alias){XCTAssertNotNil(UIImage(named:asset))}
        }
        XCTAssertNil(AgentBrand.asset("my-codex-proxy"))
        XCTAssertNil(AgentBrand.asset("new-provider"))
    }
    func testRemoteURLRejectsHostConfusionCredentialsAndSchemeDowngrade() {
        XCTAssertTrue(ZCodeRemoteURL.allowed(URL(string: "https://zcode.z.ai/remote/session")!))
        for input in ["https://zcode.z.ai.evil.test/remote/", "http://zcode.z.ai/remote/", "https://user@zcode.z.ai/remote/", "https://zcode.z.ai:444/remote/", "https://zcode.z.ai/account", "https://zcode.z.ai/remote/../../account"] {
            XCTAssertFalse(ZCodeRemoteURL.allowed(URL(string: input)!))
        }
    }
    func testRenderFrameRejectsTruncatedAndUntrustedData() {
        XCTAssertThrowsError(try TerminalFrame.decode(Data()))
        XCTAssertThrowsError(try TerminalFrame.decode(Data(repeating: 0, count: 240)))
        XCTAssertThrowsError(try TerminalFrame.decode(Data("TLRFRM4\0".utf8)))
    }
}
