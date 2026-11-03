// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class AICreateLocationTests: XCTestCase {
    private func pc(_ id: UUID, online: Bool = true, routing: Bool = true, ai: Bool = true) throws -> PC {
        try PC(.object(["id": .string(id.uuidString), "device_id": .string(UUID().uuidString),
            "online": .bool(online), "remote_routing_enabled": .bool(routing),
            "capabilities": .array(ai ? [.string("ai_agent_v1")] : [])]))
    }
    func testDirectoryLaunchKeepsTheSelectedComputerAndExactUnicodePath() throws {
        let selected = UUID(), other = UUID(), path = "D:\\项目\\space dir"
        let location = try AICreateLocation(pcID: selected, directory: path)
        try location.validate(in: [pc(other), pc(selected)])
        XCTAssertEqual(location.pcID, selected)
        XCTAssertEqual(location.directory, path)
        XCTAssertThrowsError(try location.validate(in: [pc(other)]), "Never silently create on a different online computer.")
    }
    func testOfflineRoutingDisabledAndUnsupportedSelectedComputerCannotCreate() throws {
        let selected = UUID(), other = UUID()
        let location = try AICreateLocation(pcID: selected, directory: "C:\\work")
        for blocked in [try pc(selected, online: false), try pc(selected, routing: false), try pc(selected, ai: false)] {
            XCTAssertThrowsError(try location.validate(in: [pc(other), blocked]))
        }
    }
    func testDirectoryLengthAndNULBoundsApplyBeforeOpeningCreation() throws {
        let id = UUID()
        for invalid in ["", " \n", "C:\\bad\0dir", String(repeating: "中", count: 1366)] {
            XCTAssertThrowsError(try AICreateLocation(pcID: id, directory: invalid))
        }
        XCTAssertNoThrow(try AICreateLocation(pcID: id, directory: String(repeating: "a", count: 4096)))
    }
}
