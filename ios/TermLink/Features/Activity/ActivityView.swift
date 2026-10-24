// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI

struct ActivityView: View {
    @EnvironmentObject var product: ProductStore
    @State private var confirmClear = false
    private func icon(_ kind: String) -> String {
        switch kind {
        case "lease-granted": return "hand.tap"
        case "lease-released", "lease-revoked": return "hand.raised"
        case "session-ended": return "xmark.circle"
        case "snapshot-restored": return "clock.arrow.circlepath"
        case "signed-in": return "checkmark.shield"
        case "signed-out": return "lock"
        default: return "dot.circle"
        }
    }
    var body: some View {
        List {
            Section {
                ForEach(product.activity.entries) { entry in
                    HStack(alignment: .top, spacing: 10) {
                        Image(systemName: icon(entry.kind)).foregroundStyle(.secondary).frame(width: 22)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(entry.detail)
                            Text(entry.time.formatted(date: .abbreviated, time: .shortened)).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            } header: { Text("本机活动") } footer: {
                Text("这是本机自己的控制与会话记录，不是服务器审计日志。最多保留 200 条。")
            }
            if !product.activity.entries.isEmpty {
                Section { Button("清除全部活动记录", role: .destructive) { confirmClear = true } }
            }
        }
        .navigationTitle("活动")
        .task { product.activity.restoreIfNeeded() }
        .confirmationDialog("清除本机的全部活动记录？", isPresented: $confirmClear) {
            Button("清除", role: .destructive) { product.activity.clear() }
        } message: { Text("只影响本机记录，不影响电脑与服务器。") }
    }
}
