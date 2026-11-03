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
    static func available(_ agent: JSONValue) -> Bool {
        agent["status"].string == "available" && agent["strategy"].string == "native"
    }
    static func requiresHostUpgrade(_ catalog: JSONValue, agent: String = "zcode") -> Bool {
        agent == "zcode" && catalog["agents"].array.contains {
            $0["id"].string == agent && $0["strategy"].string == "desktop_remote"
        } && !catalog["agents"].array.contains { $0["id"].string == agent && available($0) }
    }
    static func requireNativeAgent(_ catalog: JSONValue, agent: String, history: Bool = false) throws {
        if requiresHostUpgrade(catalog, agent: agent) {
            throw ClientError.unavailable("请升级电脑端以使用 ZCode 原生会话。")
        }
        guard catalog["agents"].array.contains(where: {
            $0["id"].string == agent && available($0) && (!history || $0["nativeHistory"].bool)
        }) else { throw ClientError.readOnly }
    }
}

/// Validates explicit configuration choices without inferring host capabilities.
enum AIModelConfiguration {
    static func canEdit(_ session: AIConversation) -> Bool {
        session.canControl && session.state == "idle" && session.pending.isEmpty
    }

    static func canChangeReasoning(_ session: AIConversation) -> Bool {
        let configuration = session.capabilities["configuration"]
        return configuration["reasoningSwitch"].bool && configuration["reasoningScope"].string != "new_session" &&
            session.raw["reasoning"]["status"].string == "available"
    }

    static func fields(for session: AIConversation, model: String? = nil, provider: String? = nil,
                       effort: String? = nil) throws -> [String: JSONValue] {
        guard canEdit(session) else { throw ClientError.readOnly }
        let selectedModel = model ?? session.raw["modelId"].string
        let selectedProvider = provider ?? session.raw["providerId"].string
        if model != nil || provider != nil {
            guard session.capabilities["configuration"]["modelSwitch"].bool else { throw ClientError.readOnly }
            guard validIdentifier(selectedModel, limit: 200), validIdentifier(selectedProvider, limit: 200) else {
                throw ClientError.unavailable("模型 ID 和提供方 ID 最多 200 个字符，不能只含空白或包含控制字符。")
            }
            if session.raw["agentId"].string == "zcode", selectedModel.isEmpty || selectedProvider.isEmpty {
                throw ClientError.unavailable("已有 ZCode 会话必须同时填写模型 ID 和提供方 ID，不能恢复为空的默认模型。")
            }
        }
        var result: [String: JSONValue] = ["modelId": .string(selectedModel), "providerId": .string(selectedProvider)]
        if let effort {
            guard canChangeReasoning(session) else { throw ClientError.readOnly }
            guard validIdentifier(effort, limit: 80), effort.isEmpty ||
                    session.raw["reasoning"]["options"].array.contains(where: { $0["value"].string == effort }) else {
                throw ClientError.unavailable("当前模型不支持此推理强度，请重新读取模型与推理设置。")
            }
            // nil preserves the current setting; an explicit empty value restores the host default.
            result["reasoningEffort"] = .string(effort)
        }
        return result
    }

    private static func validIdentifier(_ value: String, limit: Int) -> Bool {
        value.utf16.count <= limit && !value.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) &&
            (value.isEmpty || !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
    }
}

/// Validates explicit configuration choices without inferring host capabilities.
enum AIModelConfiguration {
    static func canEdit(_ session: AIConversation) -> Bool {
        session.canControl && session.state == "idle" && session.pending.isEmpty
    }

    static func canChangeReasoning(_ session: AIConversation) -> Bool {
        let configuration = session.capabilities["configuration"]
        return configuration["reasoningSwitch"].bool && configuration["reasoningScope"].string != "new_session" &&
            session.raw["reasoning"]["status"].string == "available"
    }

    static func fields(for session: AIConversation, model: String? = nil, provider: String? = nil,
                       effort: String? = nil) throws -> [String: JSONValue] {
        guard canEdit(session) else { throw ClientError.readOnly }
        let selectedModel = model ?? session.raw["modelId"].string
        let selectedProvider = provider ?? session.raw["providerId"].string
        if model != nil || provider != nil {
            guard session.capabilities["configuration"]["modelSwitch"].bool else { throw ClientError.readOnly }
            guard validIdentifier(selectedModel, limit: 200), validIdentifier(selectedProvider, limit: 200) else {
                throw ClientError.unavailable("模型 ID 和提供方 ID 最多 200 个字符，不能只含空白或包含控制字符。")
            }
            if session.raw["agentId"].string == "zcode", selectedModel.isEmpty || selectedProvider.isEmpty {
                throw ClientError.unavailable("已有 ZCode 会话必须同时填写模型 ID 和提供方 ID，不能恢复为空的默认模型。")
            }
        }
        var result: [String: JSONValue] = ["modelId": .string(selectedModel), "providerId": .string(selectedProvider)]
        if let effort {
            guard canChangeReasoning(session) else { throw ClientError.readOnly }
            guard validIdentifier(effort, limit: 80), effort.isEmpty ||
                    session.raw["reasoning"]["options"].array.contains(where: { $0["value"].string == effort }) else {
                throw ClientError.unavailable("当前模型不支持此推理强度，请重新读取模型与推理设置。")
            }
            // nil preserves the current setting; an explicit empty value restores the host default.
            result["reasoningEffort"] = .string(effort)
        }
        return result
    }

    private static func validIdentifier(_ value: String, limit: Int) -> Bool {
        value.utf16.count <= limit && !value.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) &&
            (value.isEmpty || !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
    }
}

struct AIModelView: View {
    @ObservedObject var workspace: AIWorkspace
    let session: AIConversation
    @EnvironmentObject private var product: ProductStore
    @Environment(\.dismiss) private var dismiss
    @State private var models: [JSONValue] = []
    @State private var loading = false
    @State private var manual = false
    @State private var manualModel = ""
    @State private var manualProvider = ""
    @State private var validationError: String?
    private var configuration: JSONValue { session.capabilities["configuration"] }
    private var editable: Bool {
        AIModelConfiguration.canEdit(session) &&
            !workspace.busy.contains(session.pcID) && workspace.uncertain[session.pcID] == nil
    }
    var body: some View {
        List {
            if loading { ProgressView() }
            Section("模型") {
                if session.raw["agentId"].string != "zcode" {
                    Button("电脑默认模型") { configure(model: "", provider: "") }
                        .disabled(!editable || !configuration["modelSwitch"].bool)
                }
                Button("手动输入模型 ID…") {
                    manualModel = session.raw["modelId"].string
                    manualProvider = session.raw["providerId"].string
                    validationError = nil; manual = true
                }.disabled(!editable || !configuration["modelSwitch"].bool)
                if !configuration["modelListing"].bool {
                    Text("电脑未提供模型列表，可在支持模型切换时手动填写 ID。").font(.caption).foregroundStyle(.secondary)
                }
                ForEach(models, id: \.self) { model in
                    Button {
                        configure(model: model["modelId"].string, provider: model["providerId"].string)
                    } label: {
                        VStack(alignment: .leading) {
                            Text(model["name"].string.isEmpty ? model["modelId"].string : model["name"].string)
                            Text(model["providerId"].string).font(.caption).foregroundStyle(.secondary)
                        }
                    }.disabled(!editable || !configuration["modelSwitch"].bool)
                }
            }
            Section("推理强度") {
                Button("恢复默认推理强度") { configure(effort: "") }
                    .disabled(!editable || !AIModelConfiguration.canChangeReasoning(session))
                ForEach(session.raw["reasoning"]["options"].array, id: \.self) { option in
                    Button(option["value"].string) {
                        configure(effort: option["value"].string)
                    }.disabled(!editable || !AIModelConfiguration.canChangeReasoning(session))
                }
                if configuration["reasoningScope"].string == "new_session" { Text("此 Agent 的推理强度只能在新建会话时设置。").font(.caption) }
            }
        }.navigationTitle("模型与推理").toolbar { Button("关闭") { dismiss() } }
            .sheet(isPresented: $manual) {
                NavigationStack {
                    Form {
                        TextField("模型 ID", text: $manualModel).textInputAutocapitalization(.never).autocorrectionDisabled()
                        TextField(session.raw["agentId"].string == "zcode" ? "提供方 ID（必填）" : "提供方 ID（可选）", text: $manualProvider)
                            .textInputAutocapitalization(.never).autocorrectionDisabled()
                        Text(session.raw["agentId"].string == "zcode" ? "已有 ZCode 会话必须同时填写模型和提供方 ID。" : "两项均留空可恢复电脑默认模型。")
                            .font(.caption).foregroundStyle(.secondary)
                        if let validationError { Text(validationError).font(.caption).foregroundStyle(.red) }
                    }
                    .navigationTitle("手动输入模型")
                    .toolbar {
                        ToolbarItem(placement: .cancellationAction) { Button("取消") { manual = false } }
                        ToolbarItem(placement: .confirmationAction) {
                            Button("应用") {
                                do {
                                    _ = try AIModelConfiguration.fields(for: session, model: manualModel, provider: manualProvider)
                                    configure(model: manualModel, provider: manualProvider)
                                } catch { validationError = error.localizedDescription }
                            }.disabled(!editable || !configuration["modelSwitch"].bool)
                        }
                    }
                }
            }
            .task {
                guard configuration["modelListing"].bool else { return }
                loading = true; defer { loading = false }
                do { models = try await workspace.models(session) } catch { product.report(error) }
            }
    }

    private func configure(model: String? = nil, provider: String? = nil, effort: String? = nil) {
        Task {
            do {
                try await workspace.configure(session, model: model, provider: provider, effort: effort)
                manual = false; dismiss()
            } catch {
                if manual { validationError = error.localizedDescription } else { product.report(error) }
            }
        }
    }
}

struct ActivityShareView: UIViewControllerRepresentable {
    let url: URL
    func makeUIViewController(context: Context) -> UIActivityViewController { UIActivityViewController(activityItems: [url], applicationActivities: nil) }
    func updateUIViewController(_ controller: UIActivityViewController, context: Context) {}
}
