// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class AIModelConfigurationTests: XCTestCase {
    private func session(agent: String = "codex", modelSwitch: Bool = true, listing: Bool = false,
                         reasoningSwitch: Bool = true, scope: String = "session", reasoningStatus: String = "available",
                         canControl: Bool = true, state: String = "idle", pending: [JSONValue] = [],
                         options: [String] = ["medium", "high"]) -> AIConversation {
        .init(raw: .object([
            "id": .string(UUID().uuidString), "agentId": .string(agent), "state": .string(state),
            "canControl": .bool(canControl), "pending": .array(pending), "controlVersion": .number(7),
            "settingsVersion": .number(3), "modelId": .string("current-model"), "providerId": .string("current-provider"),
            "reasoningEffort": .string("high"),
            "capabilities": .object(["configuration": .object([
                "modelSwitch": .bool(modelSwitch), "modelListing": .bool(listing),
                "reasoningSwitch": .bool(reasoningSwitch), "reasoningScope": .string(scope)
            ])]),
            "reasoning": .object(["status": .string(reasoningStatus), "defaultValue": .string("medium"),
                "options": .array(options.map { .object(["value": .string($0)]) })])
        ]), pcID: UUID())
    }

    func testModelSelectionOmitsEffortWhileExplicitEmptyRestoresDefault() throws {
        let current = session()
        let model = try AIModelConfiguration.fields(for: current, model: "new-model", provider: "new-provider")
        XCTAssertEqual(model["modelId"], .string("new-model"))
        XCTAssertEqual(model["providerId"], .string("new-provider"))
        XCTAssertNil(model["reasoningEffort"])
        let reset = try AIModelConfiguration.fields(for: current, effort: "")
        XCTAssertEqual(reset["reasoningEffort"], .string(""))
        XCTAssertEqual(reset["modelId"], current.raw["modelId"])
        XCTAssertEqual(reset["providerId"], current.raw["providerId"])
        XCTAssertEqual(try AIModelConfiguration.fields(for: current, effort: "high")["reasoningEffort"], .string("high"))
    }

    func testModelSwitchWorksWithoutListingButListingDoesNotGrantSwitchPermission() throws {
        let manual = try AIModelConfiguration.fields(for: session(listing: false), model: "manually-entered", provider: "")
        XCTAssertEqual(manual["modelId"], .string("manually-entered"))
        for listing in [false, true] {
            XCTAssertThrowsError(try AIModelConfiguration.fields(for: session(modelSwitch: false, listing: listing),
                model: "new", provider: "")) { error in XCTAssertEqual(error as? ClientError, .readOnly) }
        }
    }

    func testComputerDefaultIsAvailableForNonZCodeAgentsOnly() throws {
        for agent in ["codex", "claude", "opencode", "future-agent"] {
            let fields = try AIModelConfiguration.fields(for: session(agent: agent), model: "", provider: "")
            XCTAssertEqual(fields["modelId"], .string(""))
            XCTAssertEqual(fields["providerId"], .string(""))
            XCTAssertNil(fields["reasoningEffort"])
        }
        for pair in [("", ""), ("model", ""), ("", "provider")] {
            XCTAssertThrowsError(try AIModelConfiguration.fields(for: session(agent: "zcode"), model: pair.0, provider: pair.1))
        }
        let fields = try AIModelConfiguration.fields(for: session(agent: "zcode"), model: "model", provider: "provider")
        XCTAssertEqual(fields["providerId"], .string("provider"))
    }

    func testModelAndProviderRejectOverlongBlankAndControlCharacterValues() throws {
        let current = session()
        let maximum = String(repeating: "x", count: 200)
        XCTAssertEqual(try AIModelConfiguration.fields(for: current, model: maximum, provider: maximum)["modelId"], .string(maximum))
        let unicodeMaximum = String(repeating: "😀", count: 100)
        XCTAssertNoThrow(try AIModelConfiguration.fields(for: current, model: unicodeMaximum))
        for invalid in [maximum + "x", String(repeating: "😀", count: 101), " ", "\t", "model\0bad", "model\nnext", "bad\u{7f}"] {
            XCTAssertThrowsError(try AIModelConfiguration.fields(for: current, model: invalid, provider: ""))
            XCTAssertThrowsError(try AIModelConfiguration.fields(for: current, model: "model", provider: invalid))
        }
    }

    func testEffortNeedsAuthoritativeSupportAndExistingSessionScope() {
        for status in ["unknown", "unsupported", "supported", "", "AVAILABLE"] {
            for effort in ["", "high"] {
                XCTAssertThrowsError(try AIModelConfiguration.fields(for: session(reasoningStatus: status), effort: effort)) {
                    error in XCTAssertEqual(error as? ClientError, .readOnly)
                }
            }
        }
        for current in [session(reasoningSwitch: false), session(scope: "new_session")] {
            XCTAssertFalse(AIModelConfiguration.canChangeReasoning(current))
            XCTAssertThrowsError(try AIModelConfiguration.fields(for: current, effort: ""))
            XCTAssertThrowsError(try AIModelConfiguration.fields(for: current, effort: "high"))
            // A model-only mutation must still omit and preserve the effort setting.
            XCTAssertNil(try AIModelConfiguration.fields(for: current, model: "other")["reasoningEffort"])
        }
        XCTAssertNoThrow(try AIModelConfiguration.fields(for: session(scope: "next_turn"), effort: "high"))
    }

    func testExplicitDefaultDoesNotRequireAnInventedEmptyAdvertisedOption() throws {
        let current = session(options: [])
        XCTAssertEqual(try AIModelConfiguration.fields(for: current, effort: "")["reasoningEffort"], .string(""))
        XCTAssertThrowsError(try AIModelConfiguration.fields(for: current, effort: "high"))
    }

    func testEffortMustBeAdvertisedBoundedAndFreeOfControls() throws {
        let maximum = String(repeating: "x", count: 80)
        XCTAssertNoThrow(try AIModelConfiguration.fields(for: session(options: [maximum]), effort: maximum))
        XCTAssertThrowsError(try AIModelConfiguration.fields(for: session(), effort: "not-advertised"))
        for invalid in [maximum + "x", " ", "high\0", "high\n", "bad\u{85}"] {
            // Even an advertised malformed option cannot bypass local input validation.
            XCTAssertThrowsError(try AIModelConfiguration.fields(for: session(options: [invalid]), effort: invalid))
        }
    }

    func testReadOnlyBusyAndPendingSessionsCannotConfigureModelsOrEffort() {
        let blocked = [session(canControl: false), session(state: "running"), session(state: "exited"),
                       session(pending: [.object(["id": .string("approval")])])]
        for current in blocked {
            XCTAssertFalse(AIModelConfiguration.canEdit(current))
            XCTAssertThrowsError(try AIModelConfiguration.fields(for: current, model: "new-model"))
            XCTAssertThrowsError(try AIModelConfiguration.fields(for: current, effort: ""))
        }
    }
}
