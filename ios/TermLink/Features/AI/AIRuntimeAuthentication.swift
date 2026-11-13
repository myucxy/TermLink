// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI

/// Volatile provider challenge. Never persisted with drafts, history or tokens.
enum AIRuntimeAuthentication {
    static func validateReceipt(action:String,agent:String,response:JSONValue) throws {
        guard ["authentication.start","authentication.cancel"].contains(action),response["error"].string.isEmpty else{return}
        guard response["authentication"]["agentId"].string==agent,
              ["required","pending","authenticated","not_required","cancelled","failed","unknown"].contains(response["authentication"]["status"].string),
              response["authentication"]["userCode"].string.isEmpty,response["authentication"]["verificationUrl"].string.isEmpty else {
            throw ClientError.protocolViolation("runtime authorization receipt")
        }
    }
    static func summary(_ value: JSONValue) -> String {
        switch value["status"].string {
        case "authenticated": "独立运行环境已登录；模型权限仍需实际会话确认。"
        case "not_required": "当前运行环境不要求 OpenAI 登录。"
        case "pending": "请在官方页面输入设备码；完成后查询登录状态。"
        case "required", "isolated_required": "此电脑使用独立的 Agent 配置，需要单独授权，不会复用桌面账号。"
        case "cancelled": "本次授权已取消。"
        case "failed": "授权未完成，请取消后重新开始。"
        default: "尚未确认模型授权。协议可用不代表已经登录。"
        }
    }
    private static func parseExpiry(_ text:String)->Date? {
        let formatter=ISO8601DateFormatter();formatter.formatOptions=[.withInternetDateTime,.withFractionalSeconds]
        return formatter.date(from:text) ?? ISO8601DateFormatter().date(from:text)
    }
    static func challenge(_ value: JSONValue, now: Date = Date()) -> (URL, String)? {
        guard value["status"].string == "pending", value["method"].string == "device_code",
              value["verificationUrl"].string == "https://auth.openai.com/codex/device",
              let url=URL(string:value["verificationUrl"].string), let expiry=parseExpiry(value["expiresAt"].string), expiry>now else{return nil}
        let code=value["userCode"].string
        guard (4...32).contains(code.utf8.count),code.utf8.allSatisfy({(48...57).contains($0)||(65...90).contains($0)||(97...122).contains($0)||$0==45}) else{return nil}
        return (url,code)
    }
}

struct AIRuntimeAuthenticationView: View {
    @ObservedObject var workspace: AIWorkspace
    let pc: UUID, agent: String, descriptor: JSONValue
    @Environment(\.dismiss) private var dismiss
    @Environment(\.scenePhase) private var scenePhase
    @State private var boundHost=""
    @State private var value: JSONValue = .null
    @State private var busy=false
    @State private var error: String?
    var body:some View { Form {
        Text("模型账号授权").font(.headline)
        Text(AIRuntimeAuthentication.summary(value == .null ? descriptor:value))
        Text("授权只写入电脑上当前 TermLink 账号的独立 Agent 配置，不读取或复制原桌面登录。授权成功不自动发送消息或运行模型。")
            .font(.caption).foregroundStyle(.secondary)
        if !descriptor["configurationDirectory"].string.isEmpty {
            Text("电脑独立配置目录").font(.caption)
            Text(descriptor["configurationDirectory"].string).font(.caption.monospaced()).textSelection(.enabled)
        }
        if descriptor["method"].string == "device_code" {
            TimelineView(.periodic(from:.now,by:1)){context in
                if let (url,code)=AIRuntimeAuthentication.challenge(value,now:context.date) {
                    Text(code).font(.system(.title,design:.monospaced)).textSelection(.enabled)
                    Link("打开 OpenAI 官方授权页面",destination:url)
                } else if value["status"].string == "pending" {Text("设备码已过期或当前状态未确认，请重新查询或取消后开始。")}
            }
            Button("查询授权状态"){perform("authentication")}.disabled(busy || !descriptor["canCheck"].bool)
            Button("开始设备码授权"){perform("authentication.start")}.disabled(busy || !descriptor["canStart"].bool || value["status"].string == "pending")
            Button("取消本次授权",role:.destructive){perform("authentication.cancel")}.disabled(busy)
            if workspace.uncertain[pc] != nil {Button("查询上次授权请求回执"){Task{do{try await workspace.retryUncertain(pc);perform("authentication")}catch{value = .null;self.error=error.localizedDescription}}}}
        } else {
            Text("此 Agent 暂无手机授权接口。请先在电脑当前账号的独立配置中完成官方授权，再回来检测；不会借用其他应用的登录。")
        }
        if let error{Text(error).foregroundStyle(.orange)}
    }.navigationTitle("Agent 授权").toolbar{Button("关闭"){value = .null;dismiss()}}
      .task {boundHost=workspace.catalogs[pc]?["hostId"].string ?? "";if descriptor["canCheck"].bool{perform("authentication")}}
      .onChange(of:workspace.catalogs[pc]?["hostId"].string ?? ""){_,host in if !boundHost.isEmpty && boundHost != host{value = .null;dismiss()}}
      .onChange(of:workspace.unavailable.contains(pc)){_,unavailable in if unavailable{value = .null;error="电脑连接已失效，请重新确认授权状态。"}}
      .onChange(of:scenePhase){_,phase in if phase != .active{value = .null}}
    }
    private func perform(_ action:String){guard !busy else{return};busy=true;error=nil;Task{defer{busy=false};do{value=try await workspace.runtimeAuthentication(pc:pc,agent:agent,action:action)}catch{value = .null;self.error=error.localizedDescription}}}
}
