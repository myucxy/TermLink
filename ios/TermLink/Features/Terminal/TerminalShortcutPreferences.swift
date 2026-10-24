// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// Custom terminal dock shortcuts, mirroring the Android `TerminalCustomShortcut` model:
/// quick-input text snippets and modifier combinations over core key codes
/// (TL_INPUT_KEY_*: Enter 2, Backspace 3, Tab 4, Esc 5, Del 6, Home 8, End 9, PageUp 10,
/// PageDown 11, arrows 12–15, F1–F12 16–27; character keys use key 1 with a scalar).
enum TerminalShortcutKind: String, Codable { case combination, quickInput }

struct TerminalCustomShortcut: Codable, Identifiable, Equatable {
    static let maxShortcuts = 24
    static let maxQuickInput = 4096
    var id: String
    var label: String
    var kind: TerminalShortcutKind
    var text: String
    var key: UInt32
    var scalar: UInt32
    var shift: Bool
    var alt: Bool
    var control: Bool
    var modifiers: UInt32 { (shift ? 1 : 0) | (alt ? 2 : 0) | (control ? 4 : 0) }
    var canonical: String {
        var parts: [String] = []
        if control { parts.append("Ctrl") }; if alt { parts.append("Alt") }; if shift { parts.append("Shift") }
        if key == 1, let scalar = Unicode.Scalar(scalar) { parts.append(String(Character(scalar))) }
        else if let named = TerminalCustomShortcut.specialKeys.first(where: { $0.1 == key }) { parts.append(named.0) }
        else { parts.append("Key \(key)") }
        return parts.joined(separator: "+")
    }
    var displayLabel: String { label.isEmpty ? canonical : label }
    /// Mirrors Android normalization: bounded id charset, capped label, canonical combination value.
    var normalized: TerminalCustomShortcut? {
        let trimmedID = id.trimmingCharacters(in: .whitespaces)
        guard trimmedID.count <= 64, !trimmedID.isEmpty,
              trimmedID.allSatisfy({ $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" }) else { return nil }
        let trimmedLabel = String(label.trimmingCharacters(in: .whitespaces).prefix(16))
        var value = self
        value.id = trimmedID; value.label = trimmedLabel
        switch kind {
        case .quickInput:
            let taken = String(text.prefix(Self.maxQuickInput))
            guard !taken.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
            value.text = taken
        case .combination:
            guard Self.valid(key: key, scalar: scalar, shift: shift, alt: alt, control: control) else { return nil }
            if key == 1, shift { value.scalar = Self.shiftedCodePoint(scalar); value.shift = false }
        }
        return value
    }
    static func valid(key: UInt32, scalar: UInt32, shift: Bool, alt: Bool, control: Bool) -> Bool {
        if key == 1 {
            guard let unicode = Unicode.Scalar(scalar), !CharacterSet.controlCharacters.contains(unicode) else { return false }
            return shift || alt || control // A bare character adds nothing over the keyboard.
        }
        return specialKeys.contains { $0.1 == key }
    }
    /// Port of Android `terminalShiftedCodePoint` for canonical combination storage.
    static func shiftedCodePoint(_ scalar: UInt32) -> UInt32 {
        let pairs: [String: String] = ["1": "!", "2": "@", "3": "#", "4": "$", "5": "%", "6": "^", "7": "&", "8": "*",
            "9": "(", "0": ")", "-": "_", "=": "+", "[": "{", "]": "}", "\\": "|", ";": ":", "'": "\"",
            ",": "<", ".": ">", "/": "?", "`": "~"]
        guard let character = Unicode.Scalar(scalar).map(Character.init), character.unicodeScalars.count == 1 else { return scalar }
        if let shifted = pairs[String(character)] { return String(shifted).unicodeScalars.first!.value }
        return String(character).uppercased().unicodeScalars.first?.value ?? scalar
    }
    static let specialKeys: [(String, UInt32)] = [
        ("Esc", 5), ("Tab", 4), ("Enter", 2), ("⌫", 3), ("Del", 6), ("Home", 8), ("End", 9),
        ("PageUp", 10), ("PageDown", 11), ("↑", 12), ("↓", 13), ("←", 14), ("→", 15),
        ("F1", 16), ("F2", 17), ("F3", 18), ("F4", 19), ("F5", 20), ("F6", 21),
        ("F7", 22), ("F8", 23), ("F9", 24), ("F10", 25), ("F11", 26), ("F12", 27),
    ]
}

enum TerminalShortcutLayout {
    /// Balanced two-row distribution for customs, mirroring Android's shortest-row balancing.
    static func balancedRows(_ customs: [TerminalCustomShortcut]) -> (top: [TerminalCustomShortcut], bottom: [TerminalCustomShortcut]) {
        var top: [TerminalCustomShortcut] = [], bottom: [TerminalCustomShortcut] = []
        for shortcut in customs { if top.count <= bottom.count { top.append(shortcut) } else { bottom.append(shortcut) } }
        return (top, bottom)
    }
}

/// Device-local dock preferences (UserDefaults, like Android SharedPreferences: UI comfort, not secrets).
@MainActor final class TerminalShortcutStore: ObservableObject {
    private let defaults: UserDefaults
    @Published var twoRows: Bool {
        didSet { defaults.set(twoRows, forKey: "terminal-shortcuts-two-rows") }
    }
    @Published private(set) var customs: [TerminalCustomShortcut] {
        didSet { defaults.set((try? JSONEncoder().encode(customs)) ?? Data(), forKey: "terminal-shortcuts-custom") }
    }
    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        twoRows = defaults.object(forKey: "terminal-shortcuts-two-rows") as? Bool ?? true
        customs = defaults.data(forKey: "terminal-shortcuts-custom")
            .flatMap { try? JSONDecoder().decode([TerminalCustomShortcut].self, from: $0) } ?? []
    }
    func upsert(_ shortcut: TerminalCustomShortcut) throws {
        guard var value = shortcut.normalized else { throw ClientError.unavailable("快捷键配置无效，请检查名称与内容。") }
        if !customs.contains(where: { $0.id == value.id }) {
            guard customs.count < TerminalCustomShortcut.maxShortcuts else { throw ClientError.limitExceeded }
            value.id = UUID().uuidString.lowercased()
        }
        customs = (customs.filter { $0.id != value.id } + [value]).sorted { $0.id < $1.id }
    }
    func remove(_ id: String) { customs.removeAll { $0.id == id } }
}
