// SPDX-License-Identifier: GPL-3.0-only
import XCTest

/// Uses an account previously authorized by the opt-in protocol test in this
/// dedicated simulator. The UI test receives only origin and disposable path,
/// never a password or token. It does not claim browser login UI automation.
final class TermLinkSignedInLiveUITests: XCTestCase {
    private var app: XCUIApplication!
    private var server = ""
    private var workspace = ""

    override func setUpWithError() throws {
        continueAfterFailure = false
        guard let origin = ProcessInfo.processInfo.environment["TERMLINK_LIVE_UI_SERVER"],
              let directory = ProcessInfo.processInfo.environment["TERMLINK_LIVE_UI_WORKSPACE"],
              origin.hasPrefix("https://"), directory.hasPrefix("/") else {
            throw XCTSkip("Requires an explicitly prepared disposable signed-in simulator and test directory.")
        }
        server = origin; workspace = directory
        app = XCUIApplication()
        app.launchArguments = ["-AppleLanguages", "(zh-Hans)", "-AppleLocale", "zh_CN", "-server", server]
        app.launch()
    }
    private func select(_ title: String) {
        let tab = app.tabBars.buttons[title]
        if tab.exists { tab.tap() }
        else { let item = app.staticTexts[title].firstMatch; XCTAssertTrue(item.waitForExistence(timeout: 5)); item.tap() }
    }
    private func capture(_ name: String) {
        let attachment = XCTAttachment(screenshot: app.screenshot()); attachment.name = name; attachment.lifetime = .keepAlways; add(attachment)
    }
    private func statusButton(_ text: String) -> XCUIElement {
        app.buttons.matching(NSPredicate(format: "label CONTAINS %@", text)).firstMatch
    }
    /// Explicit user handoff: inspect only, retain the authorized disposable login.
    /// The xctestrun target must point to the parent's final app artifact.
    func testFinalAppCodexAuthorizationHandoffWithoutCreatingSession() throws {
        select("设置")
        XCTAssertTrue(app.staticTexts["已授权 · Relay"].waitForExistence(timeout: 30))
        XCTAssertEqual(app.textFields["HTTPS 服务地址"].value as? String, server)
        XCTAssertFalse(app.alerts.firstMatch.exists)
        select("AI")
        let create = app.buttons["新建"]
        XCTAssertTrue(create.waitForExistence(timeout: 30)); create.tap()
        XCTAssertTrue(app.navigationBars["新建 AI 对话"].waitForExistence(timeout: 10))
        let check = app.buttons["检查模型授权"]
        XCTAssertTrue(check.waitForExistence(timeout: 40))
        let picker = app.buttons.matching(NSPredicate(format: "label CONTAINS %@", "AI Agent")).firstMatch
        if picker.exists { picker.tap(); app.buttons["Codex"].firstMatch.tap() }
        let required = app.staticTexts["此电脑使用独立的 Agent 配置，需要单独授权，不会复用桌面账号。"]
        XCTAssertTrue(required.waitForExistence(timeout: 10))
        capture("final-create-codex-auth-required-no-task")
        check.tap()
        XCTAssertTrue(app.navigationBars["Agent 授权"].waitForExistence(timeout: 10))
        XCTAssertTrue(required.waitForExistence(timeout: 30))
        XCTAssertTrue(app.buttons["查询授权状态"].exists)
        XCTAssertTrue(app.buttons["开始设备码授权"].exists)
        capture("final-codex-read-only-authorization-required")
        app.navigationBars["Agent 授权"].buttons["关闭"].tap()
        app.navigationBars["新建 AI 对话"].buttons["取消"].tap()
        select("设备")
        XCTAssertTrue(app.scrollViews.buttons["CLI"].firstMatch.waitForExistence(timeout: 15))
        capture("final-handoff-device-page-signed-in")
    }
    func testSignedInMacDirectoryCLIAndSignOut() throws {
        select("设置")
        XCTAssertTrue(app.staticTexts["已授权 · Relay"].waitForExistence(timeout: 30), "The real app must restore the disposable account; no injected app state.")
        XCTAssertFalse(app.alerts.firstMatch.exists)
        XCTAssertEqual(app.textFields["HTTPS 服务地址"].value as? String, server)
        capture("live-account-restored")
        select("设备")
        let createCLI = app.scrollViews.buttons["CLI"].firstMatch
        XCTAssertTrue(createCLI.waitForExistence(timeout: 30))
        XCTAssertTrue(createCLI.isEnabled, "Mac CLI must be enabled from actual advertised capability.")
        capture("live-mac-device-capabilities")
        createCLI.tap()
        let directory = app.textFields["工作目录（电脑上的绝对路径）"]
        XCTAssertTrue(directory.waitForExistence(timeout: 5))
        directory.tap(); directory.typeText(workspace)
        let create = app.buttons["创建会话"]
        XCTAssertTrue(create.isEnabled)
        capture("live-cli-explicit-test-directory")
        create.tap()
        let readOnly = statusButton("只读")
        XCTAssertTrue(readOnly.waitForExistence(timeout: 90), "The newly created Mac CLI must finish attach/snapshot.")
        XCTAssertFalse(app.alerts.firstMatch.exists)
        capture("live-cli-native-frame")
        readOnly.tap()
        let history = app.buttons["浏览滚回历史"]
        let historyReady = XCTNSPredicateExpectation(predicate: NSPredicate(format: "isEnabled == true"), object: history)
        XCTAssertEqual(XCTWaiter.wait(for: [historyReady], timeout: 90), .completed, "The real native frame must exist before claiming a terminal UI.")
        app.buttons["请求控制权"].tap()
        let controlled = statusButton("控制中")
        XCTAssertTrue(controlled.waitForExistence(timeout: 15))
        controlled.tap(); app.buttons["粘贴"].firstMatch.tap()
        let input = app.textViews.firstMatch
        XCTAssertTrue(input.waitForExistence(timeout: 5))
        input.tap(); input.typeText("printf '%s%s\\n' 'TL_UI_' 'LIVE_OK'\n")
        app.buttons["粘贴到当前终端"].tap()
        XCTAssertTrue(controlled.waitForExistence(timeout: 15))
        capture("live-cli-paste-action")
        controlled.tap(); app.buttons["释放控制权"].tap()
        XCTAssertTrue(readOnly.waitForExistence(timeout: 15))
        readOnly.tap(); app.buttons["结束电脑上的会话"].tap()
        app.buttons["结束 SessionHost 与 Shell"].tap()
        XCTAssertTrue(app.staticTexts["打开 CLI 会话"].waitForExistence(timeout: 30))
        select("AI")
        XCTAssertTrue(app.buttons["新建"].waitForExistence(timeout: 5))
        capture("live-ai-catalog-ui")
        select("设置")
        app.buttons["退出登录"].tap()
        let sheet = app.sheets.firstMatch
        if sheet.waitForExistence(timeout: 3) { sheet.buttons["退出登录"].tap() }
        else { app.buttons.matching(identifier: "退出登录").element(boundBy: app.buttons.matching(identifier: "退出登录").count - 1).tap() }
        XCTAssertTrue(app.buttons["授权登录"].waitForExistence(timeout: 15))
        capture("live-ui-signed-out")
    }
}
