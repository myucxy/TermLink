// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// Projects both supported wire formats into the configuration consumed by the UI.
/// Legacy capability inference is limited to the same two Agents as Android.
enum AIConfiguration {
    static func configuration(_ value: JSONValue, agentID: String,
                              legacyModelListing: Bool = false, legacyModes: [JSONValue] = []) -> JSONValue {
        let legacy = value == .null && ["codex", "claude"].contains(agentID)
        var modes = permissionModes(value["permissionModes"].array)
        if modes.isEmpty && legacy { modes = permissionModes(legacyModes) }
        let scope = value["reasoningScope"].string
        return .object([
            "modelListing": .bool(legacy ? legacyModelListing : value["modelListing"].bool),
            "modelSwitch": .bool(legacy ? legacyModelListing : value["modelSwitch"].bool),
            "reasoningSwitch": .bool(value["reasoningSwitch"].bool),
            "reasoningScope": .string(["next_turn", "new_session", "session"].contains(scope) ? scope : "session"),
            "permissionSwitch": .bool(legacy ? !modes.isEmpty : value["permissionSwitch"].bool),
            "permissionModes": .array(modes)
        ])
    }

    static func catalog(_ raw: JSONValue) -> JSONValue {
        var object = raw.object
        object["agents"] = .array(raw["agents"].array.map { agent in
            var item = agent.object
            let id = agent["id"].string
            item["configuration"] = configuration(agent["configuration"], agentID: id,
                legacyModelListing: raw["modelListing"].bool,
                legacyModes: Array(raw["configurationModes"][id].array.prefix(8)))
            return .object(item)
        })
        return .object(object)
    }

    static func conversation(_ raw: JSONValue) -> JSONValue {
        var object = raw.object, capabilities = raw["capabilities"].object
        capabilities["configuration"] = configuration(raw["capabilities"]["configuration"],
            agentID: raw["agentId"].string, legacyModelListing: raw["capabilities"]["configure"].bool,
            legacyModes: raw["capabilities"]["modes"].array)
        object["capabilities"] = .object(capabilities)
        return .object(object)
    }

    private static func permissionModes(_ values: [JSONValue]) -> [JSONValue] {
        var seen = Set<String>()
        return values.prefix(32).compactMap { value in
            let rawID: String
            switch value {
            case .string(let id): rawID = id
            case .object: rawID = value["id"].string
            default: return nil
            }
            let id = rawID.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !id.isEmpty, id.utf16.count <= 80, !id.unicodeScalars.contains(where: isControl),
                  seen.insert(id).inserted else { return nil }
            return .object([
                "id": .string(id),
                "label": .string(displayText(value["label"].string, limit: 120)),
                "description": .string(displayText(value["description"].string, limit: 320)),
                // Absent or malformed confirmation metadata must not bypass confirmation.
                "requiresConfirmation": .bool(value["requiresConfirmation"] != .bool(false))
            ])
        }
    }

    private static func isControl(_ scalar: Unicode.Scalar) -> Bool {
        scalar.value <= 0x1f || (0x7f...0x9f).contains(scalar.value)
    }

    private static func displayText(_ text: String, limit: Int) -> String {
        var result = "", length = 0
        for scalar in text.unicodeScalars where !isControl(scalar) {
            let count = String(scalar).utf16.count
            guard length + count <= limit else { break }
            result.unicodeScalars.append(scalar); length += count
        }
        return result
    }
}
