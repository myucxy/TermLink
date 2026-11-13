// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import SwiftUI
import UIKit
import XCTest
@testable import TermLink

@MainActor final class AIArtifactDownloadsViewTests: XCTestCase {
    func testRecordListRendersWithoutLiveAccountAndOffersExplicitRestoreAndShare() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let cache = AIArtifactDownloadCache(scope: "view-fixture", directory: directory, key: SymmetricKey(data: Data(repeating: 8, count: 32)))
        let session = AIConversation(raw: .object(["id": .string(UUID().uuidString), "directory": .string("/fixture/project"),
            "capabilities": .object(["pathStyle": .string("posix"), "artifacts": .bool(true)])]), pcID: UUID())
        let source = try AIArtifactSource(session: session, hostID: UUID().uuidString, artifactID: "public-result.txt", version: "0:1")
        let completed = try await cache.insert(source: source); _ = try await cache.resume(completed.id)
        _ = try await cache.append(completed.id, chunk: .init(bytes: Data(), size: 0, nextOffset: 0))
        let paused = try await cache.insert(source: source); _ = try await cache.stop(paused.id, status: .paused)
        let store = AIArtifactDownloadStore()
        try await store.activate(cache: cache, confirm: { _ in XCTFail("View must not trigger confirmation") }, read: { _, _ in XCTFail("View must not fetch"); return .null })
        let controller = UIHostingController(rootView: NavigationStack { AIArtifactDownloadsView(store: store) })
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let window = UIWindow(windowScene: scene); window.frame = CGRect(x: 0, y: 0, width: 430, height: 932)
        window.rootViewController = controller; window.makeKeyAndVisible(); controller.view.layoutIfNeeded()
        try await Task.sleep(for: .milliseconds(250))
        let image = UIGraphicsImageRenderer(bounds: window.bounds).image { _ in
            XCTAssertTrue(window.drawHierarchy(in: window.bounds, afterScreenUpdates: true))
        }
        XCTAssertGreaterThan(image.size.width, 0)
        let attachment = XCTAttachment(image: image); attachment.name = "AI artifact download records — local fixture only"; attachment.lifetime = .keepAlways; add(attachment)
        XCTAssertEqual(store.records.count, 2); XCTAssertEqual(store.records.filter { $0.status == .completed }.count, 1)
        XCTAssertEqual(store.records.filter { $0.status == .paused }.count, 1)
        window.isHidden = true; window.rootViewController = nil; store.retire()
    }
}
