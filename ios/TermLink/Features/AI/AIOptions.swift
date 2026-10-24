// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI

enum AgentBrand {
    static func asset(_ value: String) -> String? {
        switch value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() {
        case "codex", "openai": "ai_agent_openai"
        case "claude", "claude code", "claude-code": "ai_agent_claude"
        case "opencode", "open code", "open-code": "ai_agent_opencode"
        case "grok", "grok build", "grok-build": "ai_agent_grok"
        case "zcode": "ai_agent_zcode"
        default: nil
        }
    }
    static func available(_ agent: JSONValue, nativeOnly: Bool = false) -> Bool {
        agent["status"].string == "available" && (agent["strategy"].string == "native" ||
            !nativeOnly && agent["strategy"].string == "desktop_remote" && agent["id"].string == "zcode")
    }
}

struct AIModelView: View {
    @ObservedObject var workspace: AIWorkspace
    let session: AIConversation
    @EnvironmentObject private var product: ProductStore
    @Environment(\.dismiss) private var dismiss
    @State private var models: [JSONValue] = [], loading = false
    private var configuration: JSONValue { session.capabilities["configuration"] }
    private var editable: Bool {
        session.canControl && session.state == "idle" && session.pending.isEmpty &&
            !workspace.busy.contains(session.pcID) && workspace.uncertain[session.pcID] == nil
    }
    var body: some View {
        List {
            if loading { ProgressView() }
            Section("模型") {
                ForEach(models, id: \.self) { model in
                    Button {
                        Task { do { try await workspace.configure(session, model: model["modelId"].string, provider: model["providerId"].string); dismiss() } catch { product.report(error) } }
                    } label: {
                        VStack(alignment: .leading) {
                            Text(model["name"].string.isEmpty ? model["modelId"].string : model["name"].string)
                            Text(model["providerId"].string).font(.caption).foregroundStyle(.secondary)
                        }
                    }.disabled(!editable || !configuration["modelSwitch"].bool)
                }
            }
            Section("推理强度") {
                ForEach(session.raw["reasoning"]["options"].array, id: \.self) { option in
                    Button(option["value"].string) {
                        Task { do { try await workspace.configure(session, effort: option["value"].string); dismiss() } catch { product.report(error) } }
                    }.disabled(!editable || !configuration["reasoningSwitch"].bool || configuration["reasoningScope"].string == "new_session")
                }
                if configuration["reasoningScope"].string == "new_session" { Text("此 Agent 的推理强度只能在新建会话时设置。").font(.caption) }
            }
        }.navigationTitle("模型与推理").toolbar { Button("关闭") { dismiss() } }
            .task {
                guard configuration["modelListing"].bool else { return }
                loading = true; defer { loading = false }
                do { models = try await workspace.models(session) } catch { product.report(error) }
            }
    }
}

struct ActivityShareView: UIViewControllerRepresentable {
    let url: URL
    func makeUIViewController(context: Context) -> UIActivityViewController { UIActivityViewController(activityItems: [url], applicationActivities: nil) }
    func updateUIViewController(_ controller: UIActivityViewController, context: Context) {}
}
