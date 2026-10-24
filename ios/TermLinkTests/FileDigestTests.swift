// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import XCTest
@testable import TermLink

final class FileDigestTests: XCTestCase {
    func testDigestChecksWholeFileAndRejectsSizeChanges() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: url) }
        let bytes = Data(repeating: 17, count: 65537)
        try bytes.write(to: url)
        let actual = try await FileDigest.sha256(url, size: UInt64(bytes.count), limit: 100000)
        XCTAssertEqual(actual, Data(SHA256.hash(data: bytes)))
        do { _ = try await FileDigest.sha256(url, size: 65536, limit: 100000); XCTFail("accepted longer file") } catch {}
        do { _ = try await FileDigest.sha256(url, size: 65538, limit: 100000); XCTFail("accepted shorter file") } catch {}
        do { _ = try await FileDigest.sha256(url, size: UInt64(bytes.count), limit: 100); XCTFail("accepted over-limit file") } catch {}
    }
}
