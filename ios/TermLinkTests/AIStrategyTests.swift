// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class AIStrategyTests: XCTestCase {
    private func agent(_ id: String = "zcode", strategy: String = "native", status: String = "available",
                       history: Bool = true) -> JSONValue {
        .object(["id": .string(id), "strategy": .string(strategy), "status": .string(status), "nativeHistory": .bool(history)])
    }

    func testZCodeUsesTheSameNativePolicyAsOtherAgents() throws {
        for id in ["codex", "claude", "zcode", "opencode", "grok"] {
            let native = agent(id), catalog = JSONValue.object(["agents": .array([native])])
            XCTAssertTrue(AgentBrand.available(native))
            XCTAssertNoThrow(try AgentBrand.requireNativeAgent(catalog, agent: id))
            XCTAssertNoThrow(try AgentBrand.requireNativeAgent(catalog, agent: id, history: true))
            for strategy in ["desktop_remote", "local_cli", "", "future_strategy"] {
                XCTAssertFalse(AgentBrand.available(agent(id, strategy: strategy)))
            }
            XCTAssertFalse(AgentBrand.available(agent(id, status: "detected")))
        }
        XCTAssertEqual(AgentBrand.asset("ZCode"), "ai_agent_zcode")
    }

    func testLegacyHostRequiresUpgradeWithoutGrantingNativeCapabilities() throws {
        let remote = agent(strategy: "desktop_remote")
        let old = JSONValue.object(["agents": .array([remote, agent("codex")])])
        XCTAssertTrue(AgentBrand.requiresHostUpgrade(old))
        XCTAssertFalse(AgentBrand.requiresHostUpgrade(old, agent: "codex"))
        XCTAssertThrowsError(try AgentBrand.requireNativeAgent(old, agent: "zcode")) { error in
            guard case ClientError.unavailable(let message) = error else { return XCTFail("Expected an upgrade notice") }
            XCTAssertTrue(message.contains("升级电脑端"))
        }
        XCTAssertThrowsError(try AgentBrand.requireNativeAgent(old, agent: "zcode", history: true))
        XCTAssertNoThrow(try AgentBrand.requireNativeAgent(old, agent: "codex"))
        let upgraded = JSONValue.object(["agents": .array([remote, agent(history: false)])])
        XCTAssertFalse(AgentBrand.requiresHostUpgrade(upgraded))
        XCTAssertNoThrow(try AgentBrand.requireNativeAgent(upgraded, agent: "zcode"))
        XCTAssertThrowsError(try AgentBrand.requireNativeAgent(upgraded, agent: "zcode", history: true))
    }

    @MainActor func testPersistedViewsRemoveOnlyLegacyStrategiesAndClearTheirSelection() throws {
        let pc = UUID(), nativeID = UUID().uuidString, remoteID = UUID().uuidString
        let original = JSONValue.array([
            .object(["pcID": .string(pc.uuidString), "id": .string(nativeID), "agentID": .string("zcode")]),
            .object(["pcID": .string(pc.uuidString), "id": .string("remote:\(pc):zcode"), "agentID": .string("zcode")]),
            .object(["pcID": .string(pc.uuidString), "id": .string(remoteID), "agentID": .string("zcode"), "strategy": .string("desktop_remote")]),
            .object(["pcID": .string(pc.uuidString), "id": .string("future"), "agentID": .string("codex"), "strategy": .string("future_strategy")]),
            .object(["pcID": .string(pc.uuidString), "id": .string("null-strategy"), "agentID": .string("codex"), "strategy": .null])
        ])
        let decoded = try JSONDecoder().decode([AIWorkspace.Opened].self, from: original.data())
        XCTAssertEqual(decoded.first?.strategy, "native", "Old native references without a strategy remain native")
        let migrated = AIWorkspace.Opened.retainingNative(decoded, selected: remoteID)
        XCTAssertEqual(migrated.opened.map(\.id), [nativeID]); XCTAssertNil(migrated.selected)
        XCTAssertEqual(migrated.opened.first?.agentID, "zcode")
        let persisted = try JSONEncoder().encode(migrated.opened)
        let restored = try JSONDecoder().decode([AIWorkspace.Opened].self, from: persisted)
        XCTAssertEqual(restored.map(\.id), [nativeID]); XCTAssertTrue(restored.allSatisfy(\.isNative))
        XCTAssertEqual(AIWorkspace.Opened.retainingNative(decoded, selected: nativeID).selected, nativeID)
        XCTAssertNil(AIWorkspace.Opened.retainingNative([], selected: "remote:unlisted:zcode").selected)
        XCTAssertEqual(AIWorkspace.Opened.retainingNative(restored, selected: nativeID).opened.map(\.id), [nativeID])
    }
}
