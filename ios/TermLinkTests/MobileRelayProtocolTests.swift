// SPDX-License-Identifier: GPL-3.0-only
import XCTest
import CryptoKit
@testable import TermLink

final class MobileRelayProtocolTests: XCTestCase {
    func testWebSocketRequestUsesServerSubprotocolAndPreservesSignedTarget() throws {
        for (channel, path, expected) in [
            (MobileRelayProtocol.Channel.realtime, "/v1/realtime", "termlink.realtime.v1"),
            (.files, "/v1/file-transfers", "termlink.file-transfer.v2")
        ] {
            var signed = URLRequest(url: URL(string: "https://example.test:8910" + path)!)
            signed.httpMethod = "GET"
            signed.setValue("test-signature", forHTTPHeaderField: "X-TermLink-Signature")
            signed.setValue("Bearer fixture", forHTTPHeaderField: "Authorization")
            let socket = try MobileRelayProtocol.webSocketRequest(signed, channel: channel)
            XCTAssertEqual(socket.url?.absoluteString, "wss://example.test:8910" + path)
            XCTAssertEqual(socket.value(forHTTPHeaderField: "Sec-WebSocket-Protocol"), expected)
            XCTAssertEqual(socket.httpMethod, signed.httpMethod)
            XCTAssertEqual(socket.value(forHTTPHeaderField: "X-TermLink-Signature"), "test-signature")
            XCTAssertEqual(socket.value(forHTTPHeaderField: "Authorization"), "Bearer fixture")
        }
    }
    func testWebSocketRequestRejectsInsecureAndCredentialBearingOrigins() {
        for url in ["http://example.test/v1/realtime", "https://user:pass@example.test/v1/file-transfers", "file:///v1/realtime"] {
            XCTAssertThrowsError(try MobileRelayProtocol.webSocketRequest(URLRequest(url: URL(string: url)!), channel: .files))
        }
    }
    func testEncryptedEnvelopeUsesZeroAckForInputAndAllNonAckControls() throws {
        let key=SymmetricKey(data:Data(repeating:7,count:32)),session=UUID(),sender=UUID(),recipient=UUID()
        var body=ProtoMessage();body.set(1,Data("fixture".utf8))
        for type in [WireType.input,.resize,.terminate,.historyRequest,.historyControl,.catalogRequest,.aiRequest,.unsubscribe] {
            let envelope=try SessionCrypto.envelope(id:session,sender:sender,recipient:recipient,type:type,sequence:123,
                ack:99,keyID:Data("epoch-00000001".utf8),key:key,payload:body.encoded())
            let wire=try ProtoMessage(envelope.encoded())
            XCTAssertEqual(wire.uint(7),0,"Go Relay validEnvelopeACK rejects nonzero ACK for \(type).")
            XCTAssertEqual(try SessionCrypto.open(wire.bytes(11),key:key,nonce:wire.bytes(10),aad:wire.encoded(excluding:[11])),body.encoded(),
                "ACK normalization must precede AEAD authentication.")
        }
    }
    func testOnlySubscribeOutputAckAndFlowCarryCumulativeAck() throws {
        let key=SymmetricKey(data:Data(repeating:7,count:32))
        for type in [WireType.subscribe,.ack,.flow] {
            let envelope=try SessionCrypto.envelope(id:UUID(),sender:UUID(),recipient:UUID(),type:type,sequence:1,
                ack:99,keyID:Data("epoch-00000001".utf8),key:key,payload:Data())
            XCTAssertEqual(try ProtoMessage(envelope.encoded()).uint(7),99)
        }
    }

}
