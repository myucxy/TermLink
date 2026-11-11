// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class TerminalCreationTests: XCTestCase {
    private func pc(platform:String="macos",capabilities:[String],online:Bool=true,routing:Bool=true,commands:Bool=true) throws -> PC {
        try PC(.object(["id":.string(UUID().uuidString),"device_id":.string(UUID().uuidString),"platform":.string(platform),
            "online":.bool(online),"remote_routing_enabled":.bool(routing),"capabilities":.array(capabilities.map(JSONValue.string)),
            "terminal_commands":.array(commands ? [.object(["id":.string("zsh"),"display_name":.string("Z shell")])]:[])]))
    }
    func testTerminalOnlyMacCanCreateWithoutEnablingFiles() throws {
        let target=try pc(capabilities:["realtime_terminal_v1","terminal_commands_v1"])
        XCTAssertTrue(target.canCreateTerminal)
        XCTAssertFalse(target.canBrowseFiles)
        let legacyWindows=try pc(platform:"windows",capabilities:["file_operations_v1","terminal_commands_v1"])
        XCTAssertTrue(legacyWindows.canCreateTerminal)
        XCTAssertTrue(legacyWindows.canBrowseFiles)
    }
    func testTerminalCreationRequiresExactPlatformCapabilitiesCommandsAndOnlineRouting() throws {
        for target in [try pc(capabilities:[]),try pc(capabilities:["realtime_terminal_v1"]),try pc(capabilities:["terminal_commands_v1"]),
                       try pc(platform:"linux",capabilities:["realtime_terminal_v1","terminal_commands_v1"]),
                       try pc(capabilities:["realtime_terminal_v1","terminal_commands_v1"],online:false),
                       try pc(capabilities:["realtime_terminal_v1","terminal_commands_v1"],routing:false),
                       try pc(capabilities:["realtime_terminal_v1","terminal_commands_v1"],commands:false)] {
            XCTAssertFalse(target.canCreateTerminal)
        }
        XCTAssertFalse(try pc(capabilities:["file_operations_v1"],online:false).canBrowseFiles)
        XCTAssertFalse(try pc(capabilities:["file_operations_v1"],routing:false).canBrowseFiles)
    }
}
