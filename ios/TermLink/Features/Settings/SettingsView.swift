// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI
import UserNotifications

struct SettingsView: View {
    @EnvironmentObject private var product: ProductStore
    @Environment(\.openURL) private var openURL
    @AppStorage("appearance") private var appearance = "system"
    @AppStorage("terminal-font-size") private var terminalFont = 14.0
    @AppStorage("ai-font-scale") private var aiScale = 1.0
    @AppStorage("notification-sound") private var sound = false
    @AppStorage("keep-screen-on") private var keepScreenOn = false
    @State private var notifications = false
    @State private var confirmSignOut = false

    var body: some View {
        Form {
            Section {
                TextField("HTTPS 服务地址", text: $product.server)
                    .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                    .disabled(product.signedIn || product.signingIn)
                if product.signedIn {
                    Label("已授权 · Relay", systemImage: "checkmark.shield")
                    LabeledContent("账号", value: product.accountID)
                    Button("重新确认连接") { Task { await product.reconnect() } }
                    NavigationLink("设备管理") { AccountDevicesView() }
                    Button("账号安全与密码") { openAccountPage(.security) }
                    Button("账号隐私与注销") { openAccountPage(.privacy) }
                    Button("退出登录", role: .destructive) { confirmSignOut = true }
                } else {
                    Button(product.signingIn ? "等待授权…" : "授权登录") { Task { await product.signIn() } }
                        .disabled(product.signingIn)
                    Button("注册账号") { openAccountPage(.registration) }.disabled(product.signingIn)
                    Button("登录帮助与找回密码") { openAccountPage(.loginHelp) }.disabled(product.signingIn)
                }
            } header: { Text("连接与账号") } footer: {
                Text("账号操作在当前服务的系统浏览器页面完成，可能需要再次登录。注册完成后返回应用授权登录。")
            }
            Section("显示") {
                Picker("外观", selection: $appearance) {
                    Text("跟随系统").tag("system"); Text("浅色").tag("light"); Text("深色").tag("dark")
                }
                Toggle("屏幕常亮", isOn: $keepScreenOn).onChange(of: keepScreenOn) { _, enabled in
                    UIApplication.shared.isIdleTimerDisabled = enabled
                }
                LabeledContent("终端字号", value: "\(Int(terminalFont))")
                Slider(value: $terminalFont, in: 10...28, step: 1).accessibilityLabel("终端字号")
                LabeledContent("AI 字体", value: "\(Int(aiScale * 100))%")
                Slider(value: $aiScale, in: 0.85...2, step: 0.05).accessibilityLabel("AI 字体缩放")
            }
            Section("终端") {
                NavigationLink("快捷键") { ShortcutManagerView() }
            }
            Section {
                Toggle("AI 状态通知", isOn: Binding(get: { notifications }, set: { enabled in
                    Task { do { notifications = try await product.notifications.setEnabled(enabled) } catch { product.report(error) } }
                }))
                Toggle("通知声音", isOn: $sound).disabled(!notifications)
                Link("打开系统通知设置", destination: URL(string: UIApplication.openSettingsURLString)!)
            } header: { Text("通知与声音") } footer: {
                Text("状态通知不包含对话正文。当前在前台连接期间检测状态；iOS 进入后台后暂停连接。剪贴板只在点击复制或粘贴时访问。")
            }
            Section("关于") {
                LabeledContent("版本", value: Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "DEV")
                LabeledContent("终端核心", value: TLTerminalBridge.available ? "已接入" : "缺少 iOS 二进制")
                NavigationLink("开源许可与致谢") { LicenseListView() }
                Link("项目主页", destination: URL(string: "https://github.com/myucxy/TermLink")!)
                Text("TermLink · GPL-3.0-only").foregroundStyle(.secondary)
            }
        }
        .navigationTitle("设置")
        .task { notifications = await product.notifications.isEnabled() }
        .confirmationDialog("退出当前账号？", isPresented: $confirmSignOut) {
            Button("退出登录", role: .destructive) { Task { await product.signOut() } }
        } message: { Text("断开本机连接；电脑上的会话继续运行。") }
    }

    private func openAccountPage(_ page: AccountWebPage) {
        do { openURL(try page.url(server: product.server)) }
        catch { product.report(error) }
    }
}

struct AccountDevicesView: View {
    @EnvironmentObject private var product: ProductStore
    @State private var revoke: UUID?
    var body: some View {
        List(product.devices) { device in
            HStack {
                VStack(alignment: .leading) { Text(device.name); Text(device.trust).font(.caption).foregroundStyle(.secondary) }
                Spacer()
                if device.id == product.identity?.id { Text("本机").font(.caption) }
                else if device.trust != "REVOKED" { Button("撤销", role: .destructive) { revoke = device.id } }
            }
        }
        .navigationTitle("设备管理")
        .confirmationDialog("撤销此设备的访问权限？", isPresented: Binding(get: { revoke != nil }, set: { if !$0 { revoke = nil } })) {
            Button("撤销设备", role: .destructive) {
                guard let id = revoke, let api = product.api else { return }; revoke = nil
                Task { do { _ = try await api.request("/v1/devices/\(id)/revoke", method: "POST", body: .object([:]), idempotency: UUID()); try await product.refresh() } catch { product.report(error) } }
            }
        }
    }
}

struct LicenseListView: View {
    private var files: [URL] {
        let directory = Bundle.main.url(forResource: "licenses", withExtension: nil)
        let shared = directory.flatMap { try? FileManager.default.contentsOfDirectory(at: $0, includingPropertiesForKeys: nil) } ?? []
        let core = ["terminal-core-LICENSE", "terminal-core-NOTICE", "terminal-core-THIRD-PARTY-NOTICES"]
            .compactMap { Bundle.main.url(forResource: $0, withExtension: "txt") }
        return (shared + core).filter { ["txt", "md"].contains($0.pathExtension) }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
    }
    var body: some View {
        List(files, id: \.path) { file in
            NavigationLink(file.lastPathComponent) {
                ScrollView { Text((try? String(contentsOf: file, encoding: .utf8)) ?? "无法读取许可文件。")
                    .font(.system(.caption, design: .monospaced)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding() }
                    .navigationTitle(file.lastPathComponent).navigationBarTitleDisplayMode(.inline)
            }
        }.navigationTitle("许可与致谢")
    }
}
