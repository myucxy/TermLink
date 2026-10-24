// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI

struct ShortcutManagerView: View {
    @EnvironmentObject var shortcuts: TerminalShortcutStore
    @State private var editing: TerminalCustomShortcut?
    @State private var removed: TerminalCustomShortcut?
    var body: some View {
        Form {
            Section {
                Picker("Dock 布局", selection: $shortcuts.twoRows) {
                    Text("单行").tag(false); Text("两行").tag(true)
                }.pickerStyle(.segmented)
            } footer: { Text("自定义快捷键会自动分配到 Dock 行；最多 \(TerminalCustomShortcut.maxShortcuts) 个。") }
            Section {
                ForEach(shortcuts.customs) { shortcut in
                    Button { editing = shortcut } label: {
                        HStack {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(shortcut.displayLabel).foregroundStyle(.primary)
                                Text(shortcut.kind == .quickInput ? String(shortcut.text.prefix(60)) : shortcut.canonical)
                                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                            }
                            Spacer()
                            Image(systemName: "chevron.right").font(.caption).foregroundStyle(.tertiary)
                        }
                    }
                }.onDelete { offsets in
                    let victims = offsets.compactMap { shortcuts.customs.indices.contains($0) ? shortcuts.customs[$0] : nil }
                    guard let first = victims.first else { return }
                    victims.forEach { shortcuts.remove($0.id) }; removed = first
                }
                Button { editing = TerminalCustomShortcut(id: UUID().uuidString.lowercased(), label: "", kind: .quickInput,
                    text: "", key: 1, scalar: 0, shift: false, alt: false, control: false) } label: { Label("添加快捷键", systemImage: "plus") }
            } header: { Text("自定义快捷键") }
        }
        .navigationTitle("快捷键")
        .sheet(item: $editing) { shortcut in
            NavigationStack { ShortcutEditorView(original: shortcut) }
        }
        .undoPrompt(item: $removed) { shortcuts.upsert($0) }
    }
}

extension View {
    /// One-step undo for swipe-to-delete, so a mis-swipe never loses a configured shortcut.
    func undoPrompt(item: Binding<TerminalCustomShortcut?>, restore: @escaping (TerminalCustomShortcut) throws -> Void) -> some View {
        confirmationDialog("已删除「\(item.wrappedValue?.displayLabel ?? "")」", isPresented: Binding(
            get: { item.wrappedValue != nil }, set: { if !$0 { item.wrappedValue = nil } })) {
            Button("撤销删除") { if let value = item.wrappedValue { try? restore(value) } }
            Button("关闭", role: .cancel) { item.wrappedValue = nil }
        } message: { Text("删除的快捷键可以从撤销恢复一次。") }
    }
}

struct ShortcutEditorView: View {
    let original: TerminalCustomShortcut
    @EnvironmentObject var shortcuts: TerminalShortcutStore
    @Environment(\.dismiss) private var dismiss
    @State private var kind: TerminalShortcutKind
    @State private var label = ""
    @State private var text = ""
    @State private var baseKey: UInt32 = 1
    @State private var character = ""
    @State private var shift = false, alt = false, control = false
    init(original: TerminalCustomShortcut) {
        self.original = original
        _kind = State(initialValue: original.kind); _label = State(initialValue: original.label)
        _text = State(initialValue: original.text); _baseKey = State(initialValue: original.key)
        _shift = State(initialValue: original.shift); _alt = State(initialValue: original.alt); _control = State(initialValue: original.control)
        if original.key == 1, let scalar = Unicode.Scalar(original.scalar) { _character = State(initialValue: String(Character(scalar))) }
    }
    var body: some View {
        Form {
            Picker("类型", selection: $kind) { Text("快捷输入").tag(TerminalShortcutKind.quickInput); Text("组合键").tag(TerminalShortcutKind.combination) }
            TextField("显示名称（留空自动生成）", text: $label)
            if kind == .quickInput {
                TextEditor(text: $text).frame(minHeight: 100)
                    .accessibilityLabel("快捷输入内容")
            } else {
                Section("修饰键") {
                    Toggle("Ctrl", isOn: $control); Toggle("Alt", isOn: $alt); Toggle("Shift", isOn: $shift)
                }
                Section("基础键") {
                    Picker("特殊键", selection: $baseKey) {
                        Text("字符").tag(UInt32(1))
                        ForEach(TerminalCustomShortcut.specialKeys, id: \.1) { key in Text(key.0).tag(key.1) }
                    }
                    if baseKey == 1 {
                        TextField("单个字符，如 a、1、[", text: $character).textInputAutocapitalization(.never).autocorrectionDisabled()
                    }
                }
            }
            Button("保存") {
                var value = original
                value.kind = kind; value.label = label; value.text = text
                value.shift = shift; value.alt = alt; value.control = control
                if kind == .combination {
                    value.key = baseKey
                    value.scalar = baseKey == 1 ? (character.unicodeScalars.count == 1 ? character.unicodeScalars.first!.value : 0) : 0
                }
                do { try shortcuts.upsert(value); dismiss() } catch { failure = error.localizedDescription }
            }
        }
        .navigationTitle(original == emptyTemplate ? "添加快捷键" : "编辑快捷键")
        .toolbar { Button("取消") { dismiss() } }
        .alert("无法保存快捷键", isPresented: Binding(get: { failure != nil }, set: { if !$0 { failure = nil } })) {
            Button("好", role: .cancel) { failure = nil }
        } message: { Text(failure ?? "") }
    }
    @State private var failure: String?
    private var emptyTemplate: TerminalCustomShortcut {
        TerminalCustomShortcut(id: original.id, label: "", kind: .quickInput, text: "", key: 1, scalar: 0, shift: false, alt: false, control: false)
    }
}
