// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class AIConfigurationTests: XCTestCase {
    func testLegacyCatalogRestoresOnlyCodexAndClaudeConfiguration() {
        let raw: JSONValue = .object([
            "hostId": .string("host"), "modelListing": .bool(true),
            "configurationModes": .object([
                "codex": .array([.string(" ask "), .string("full-access")]),
                "claude": .array([.string("default")]),
                "opencode": .array([.string("full-access")])
            ]),
            "agents": .array(["codex", "claude", "opencode"].map { .object(["id": .string($0)]) }),
            "sessions": .array([.object(["id": .string("session")])])
        ])
        let result = AIConfiguration.catalog(raw), agents = result["agents"].array
        XCTAssertEqual(result["hostId"], raw["hostId"])
        XCTAssertEqual(result["sessions"], raw["sessions"])
        XCTAssertTrue(agents[0]["configuration"]["modelListing"].bool)
        XCTAssertTrue(agents[0]["configuration"]["modelSwitch"].bool)
        XCTAssertTrue(agents[0]["configuration"]["permissionSwitch"].bool)
        XCTAssertEqual(agents[0]["configuration"]["permissionModes"].array.map { $0["id"].string }, ["ask", "full-access"])
        XCTAssertTrue(agents[0]["configuration"]["permissionModes"].array.allSatisfy { $0["requiresConfirmation"].bool })
        XCTAssertTrue(agents[1]["configuration"]["permissionSwitch"].bool)
        assertDisabled(agents[2]["configuration"])
        XCTAssertEqual(AIConfiguration.catalog(result), result)
    }

    func testLegacyConversationUsesItsOwnCapabilitiesAndPreservesControlState() {
        let raw: JSONValue = .object([
            "id": .string("session"), "agentId": .string("claude"), "canControl": .bool(false),
            "controlVersion": .number(7), "settingsVersion": .number(4), "state": .string("running"),
            "pending": .array([.object(["id": .string("approval")])]),
            "capabilities": .object(["configure": .bool(true), "history": .bool(true),
                "modes": .array([.string("ask"), .string("acceptEdits")])])
        ])
        let result = AIConfiguration.conversation(raw), config = result["capabilities"]["configuration"]
        XCTAssertTrue(config["modelListing"].bool)
        XCTAssertTrue(config["modelSwitch"].bool)
        XCTAssertTrue(config["permissionSwitch"].bool)
        XCTAssertFalse(config["reasoningSwitch"].bool)
        XCTAssertEqual(config["reasoningScope"].string, "session")
        XCTAssertEqual(result["capabilities"]["history"], .bool(true))
        for field in ["id", "canControl", "controlVersion", "settingsVersion", "state", "pending"] {
            XCTAssertEqual(result[field], raw[field])
        }
        XCTAssertEqual(AIConfiguration.conversation(result), result)
    }

    func testExplicitConfigurationCannotBeOverriddenByLegacyFlags() {
        let explicitValues: [JSONValue] = [.object([:]), .bool(false), .array([]), .string("invalid"),
            .object(["modelListing": .bool(false), "modelSwitch": .bool(false),
                     "permissionSwitch": .bool(false), "permissionModes": .array([])])]
        for explicit in explicitValues {
            let config = AIConfiguration.configuration(explicit, agentID: "codex",
                legacyModelListing: true, legacyModes: [.string("full-access")])
            assertDisabled(config)
        }
    }

    func testModernConfigurationWorksForAnyAdvertisedAgentWithoutInferringOtherFlags() {
        let explicit: JSONValue = .object([
            "modelListing": .bool(true), "modelSwitch": .bool(false), "reasoningSwitch": .bool(true),
            "reasoningScope": .string("new_session"), "permissionSwitch": .bool(true),
            "permissionModes": .array([.object(["id": .string("ask"), "label": .string("Ask first"),
                "description": .string("Confirm each action"), "requiresConfirmation": .bool(false)])])
        ])
        let result = AIConfiguration.configuration(explicit, agentID: "future-agent")
        XCTAssertTrue(result["modelListing"].bool)
        XCTAssertFalse(result["modelSwitch"].bool)
        XCTAssertTrue(result["reasoningSwitch"].bool)
        XCTAssertEqual(result["reasoningScope"].string, "new_session")
        XCTAssertTrue(result["permissionSwitch"].bool)
        XCTAssertEqual(result["permissionModes"], explicit["permissionModes"])
    }

    func testUnknownAgentAndCaseVariantNeverGainLegacyCapabilities() {
        for agent in ["opencode", "zcode", "future-agent", "Codex", "claude-code", ""] {
            assertDisabled(AIConfiguration.configuration(.null, agentID: agent,
                legacyModelListing: true, legacyModes: [.string("full-access")]))
        }
        let legacy = AIConfiguration.configuration(.null, agentID: "codex")
        assertDisabled(legacy)
    }

    func testPermissionModesRejectInvalidIDsDeduplicateAndDefaultToConfirmation() {
        let config = AIConfiguration.configuration(.object(["permissionModes": .array([
            .null, .number(3), .bool(true), .string("   "), .string("bad\u{0}id"),
            .string("bad\u{85}id"), .string(String(repeating: "x", count: 81)),
            .string(" ask "), .object(["id": .string("ask"), "requiresConfirmation": .bool(false)]),
            .object(["id": .string("full-access"), "label": .string("Full\naccess"),
                     "description": .string("Read\tand write"), "requiresConfirmation": .string("false")])
        ])]), agentID: "codex")
        let modes = config["permissionModes"].array
        XCTAssertEqual(modes.map { $0["id"].string }, ["ask", "full-access"])
        XCTAssertTrue(modes.allSatisfy { $0["requiresConfirmation"].bool })
        XCTAssertEqual(modes[1]["label"].string, "Fullaccess")
        XCTAssertEqual(modes[1]["description"].string, "Readand write")
        XCTAssertFalse(config["permissionSwitch"].bool)
    }

    func testModeBoundsMatchAndroidUTF16AndInputCountLimits() {
        let modern = AIConfiguration.configuration(.object(["permissionModes": .array((0..<40).map {
            .object(["id": .string("mode-\($0)"), "label": .string(String(repeating: "😀", count: 100)),
                     "description": .string(String(repeating: "x", count: 400))])
        })]), agentID: "codex")
        XCTAssertEqual(modern["permissionModes"].array.count, 32)
        XCTAssertEqual(modern["permissionModes"].array[0]["label"].string.utf16.count, 120)
        XCTAssertEqual(modern["permissionModes"].array[0]["description"].string.utf16.count, 320)
        let ids = AIConfiguration.configuration(.object(["permissionModes": .array([
            .string(String(repeating: "😀", count: 40)), .string(String(repeating: "😀", count: 41))
        ])]), agentID: "codex")
        XCTAssertEqual(ids["permissionModes"].array.count, 1)
        let catalog = AIConfiguration.catalog(.object([
            "agents": .array([.object(["id": .string("codex")])]),
            "configurationModes": .object(["codex": .array((0..<12).map { .string("mode-\($0)") })])
        ]))
        XCTAssertEqual(catalog["agents"].array[0]["configuration"]["permissionModes"].array.count, 8)
    }

    func testInvalidReasoningScopeAndMalformedFlagsFailClosed() {
        let result = AIConfiguration.configuration(.object([
            "modelListing": .string("true"), "modelSwitch": .number(1),
            "reasoningSwitch": .string("true"), "reasoningScope": .string("global"),
            "permissionSwitch": .string("true")
        ]), agentID: "codex", legacyModelListing: true, legacyModes: [.string("full-access")])
        assertDisabled(result)
        XCTAssertEqual(result["reasoningScope"].string, "session")
        for scope in ["next_turn", "new_session", "session"] {
            XCTAssertEqual(AIConfiguration.configuration(.object(["reasoningScope": .string(scope)]),
                agentID: "codex")["reasoningScope"].string, scope)
        }
    }

    private func assertDisabled(_ configuration: JSONValue, file: StaticString = #filePath, line: UInt = #line) {
        for field in ["modelListing", "modelSwitch", "reasoningSwitch", "permissionSwitch"] {
            XCTAssertFalse(configuration[field].bool, field, file: file, line: line)
        }
        XCTAssertTrue(configuration["permissionModes"].array.isEmpty, file: file, line: line)
    }
}
