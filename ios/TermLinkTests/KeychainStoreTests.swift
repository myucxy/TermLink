// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class KeychainStoreTests: XCTestCase {
    func testSignedApplicationCanReadUpdateAndRemoveItsOwnKeychainItem() throws {
        let store = KeychainStore()
        let account = "test:" + UUID().uuidString
        defer { try? store.remove(account) }
        XCTAssertNil(try store.read(account))
        let first = Data("keychain-fixture-1".utf8)
        try store.write(first, account: account)
        XCTAssertEqual(try store.read(account), first)
        let updated = Data("keychain-fixture-2".utf8)
        try store.write(updated, account: account)
        XCTAssertEqual(try store.read(account), updated)
        try store.remove(account)
        XCTAssertNil(try store.read(account))
    }
}
