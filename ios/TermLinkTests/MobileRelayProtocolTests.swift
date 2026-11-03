// SPDX-License-Identifier: GPL-3.0-only
import XCTest
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
}
