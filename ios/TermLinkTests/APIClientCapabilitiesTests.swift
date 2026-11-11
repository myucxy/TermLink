// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class APIClientCapabilitiesTests: XCTestCase {
    func testSignedPCCatalogOptsIntoMacAndEnhancedWindowsShells() async throws {
        let root = URL(string: "https://capabilities.invalid")!
        let api = APIClient(root: root, identity: DeviceIdentity(), tokens: try TokenStore(scope: UUID().uuidString))
        let request = try await api.signedRequest(path: "/v1/pcs", method: "GET", token: "fixture")
        XCTAssertEqual(request.value(forHTTPHeaderField: "X-TermLink-PC-Shell-Capabilities"), "posix-shells-v1")
        XCTAssertEqual(request.value(forHTTPHeaderField: "X-TermLink-PC-Command-Capabilities"), "powershell-7-enhanced")
        XCTAssertNotNil(request.value(forHTTPHeaderField: "X-TermLink-Signature"))
        XCTAssertEqual(request.url?.path, "/v1/pcs")
    }

    func testCatalogOptInIsLimitedToExactGetRoute() async throws {
        let api = APIClient(root: URL(string: "https://capabilities.invalid")!, identity: DeviceIdentity(), tokens: try TokenStore(scope: UUID().uuidString))
        for (path, method) in [("/v1/pcs", "POST"), ("/v1/pcs/123", "GET"), ("/v1/devices", "GET")] {
            let request = try await api.signedRequest(path: path, method: method, token: "fixture")
            XCTAssertNil(request.value(forHTTPHeaderField: "X-TermLink-PC-Shell-Capabilities"))
            XCTAssertNil(request.value(forHTTPHeaderField: "X-TermLink-PC-Command-Capabilities"))
        }
    }
}
