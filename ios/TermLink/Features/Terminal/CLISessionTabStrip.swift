// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI

struct CLISessionTabItem: Identifiable, Equatable {
    let id: UUID
    let label: String

    init(id: UUID, computerName: String?, title: String, shell: String) {
        self.id = id
        let computer = computerName?.trimmingCharacters(in: .whitespacesAndNewlines)
        let session = title.isEmpty ? shell : title
        label = "\(computer?.isEmpty == false ? computer! : "电脑") · \(session.isEmpty ? "终端" : session)"
    }
}

/// Presentation only. Selection/control and opening order belong to ProductStore.
struct CLISessionTabStrip: View {
    let items: [CLISessionTabItem]
    let selected: UUID?
    let onSelect: (UUID) -> Void
    let onClose: (UUID) -> Void

    var body: some View {
        ScrollViewReader { reader in
            ScrollView(.horizontal) {
                HStack(spacing: 6) {
                    ForEach(items) { item in
                        tab(item).id(item.id)
                    }
                }.padding(.horizontal, 8)
            }
            .scrollIndicators(.hidden)
            .frame(height: 44)
            .accessibilityIdentifier("cli-session-tabs")
            .onAppear { if let selected { reader.scrollTo(selected, anchor: .center) } }
            .onChange(of: selected) { _, id in
                if let id { withAnimation(.easeInOut(duration: 0.18)) { reader.scrollTo(id, anchor: .center) } }
            }
            .onChange(of: items.map(\.id)) { _, _ in
                if let selected { reader.scrollTo(selected, anchor: .center) }
            }
        }
    }

    private func tab(_ item: CLISessionTabItem) -> some View {
        let active = selected == item.id
        return HStack(spacing: 0) {
            Button { onSelect(item.id) } label: {
                Text(item.label).font(.subheadline.weight(.medium)).lineLimit(1).truncationMode(.middle)
                    .padding(.leading, 10).padding(.trailing, 4)
                    .frame(minWidth: 108, maxWidth: 228, minHeight: 44, alignment: .leading).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(item.label)
            .accessibilityAddTraits(active ? .isSelected : [])
            .accessibilityIdentifier("cli-session-tab-\(item.id.uuidString)")
            Button { onClose(item.id) } label: {
                Image(systemName: "xmark").font(.system(size: 12, weight: .medium))
                    .frame(width: 32, height: 44).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel("关闭 \(item.label) 的视图")
            .accessibilityHint("只关闭本机标签，不结束电脑上的会话。")
            .accessibilityIdentifier("cli-session-close-\(item.id.uuidString)")
        }
        .foregroundStyle(active ? Color.primary : Color.secondary)
        .frame(minWidth: 140, maxWidth: 260, minHeight: 44)
        .background(alignment: .center) {
            RoundedRectangle(cornerRadius: 8).fill(active ? Color.accentColor.opacity(0.18) : Color.clear).frame(height: 32)
        }
        .overlay {
            RoundedRectangle(cornerRadius: 8).stroke(active ? Color.accentColor.opacity(0.65) : Color.secondary.opacity(0.4), lineWidth: 1).frame(height: 32)
                .allowsHitTesting(false)
        }
    }
}

#if DEBUG
/// Uses the production tab strip with local presentation data and no terminal/credentials.
struct CLISessionTabsUIFixture: View {
    @State private var selected: UUID? = UUID(uuidString: "11111111-1111-4111-8111-000000000001")!
    @State private var closed = 0
    @State private var items: [CLISessionTabItem] = (1...8).map { index in
        .init(id: UUID(uuidString: "11111111-1111-4111-8111-\(String(format: "%012d", index))")!,
              computerName: index.isMultiple(of: 2) ? "Windows 工作站" : "Mac 开发电脑",
              title: index == 2 ? "很长的项目会话名称用于检查中间省略和标签宽度限制" : "会话 \(index)", shell: "zsh")
    }
    var body: some View {
        VStack(spacing: 18) {
            CLISessionTabStrip(items: items, selected: selected, onSelect: { selected = $0 }, onClose: { id in
                items.removeAll { $0.id == id }; closed += 1
                if selected == id { selected = items.first?.id }
            })
            Text(items.first(where: { $0.id == selected })?.label ?? "没有会话").accessibilityIdentifier("cli-tabs-fixture-selected")
            Text("已关闭视图：\(closed)").accessibilityIdentifier("cli-tabs-fixture-closed")
            Button("选择最后一个会话") { selected = items.last?.id }
            Spacer()
        }.padding(.top).navigationTitle("CLI 标签隔离测试").navigationBarTitleDisplayMode(.inline)
    }
}
#endif
