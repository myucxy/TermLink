// SPDX-License-Identifier: GPL-3.0-only
import XCTest

/// Run on a dedicated simulator with an isolated server preference. These tests
/// never authorize a browser session, register a device or contact a host.
final class TermLinkUITests: XCTestCase {
    private var app: XCUIApplication!

    override func setUpWithError() throws {
        continueAfterFailure = false
        app = XCUIApplication()
        // The argument domain overrides the stored server for this launch only,
        // so a previously saved real account cannot auto-connect during UI smoke.
        app.launchArguments = ["-AppleLanguages", "(zh-Hans)", "-AppleLocale", "zh_CN", "-server", "https://ui-smoke.invalid"]
        app.launch()
        XCTAssertTrue(app.buttons["授权登录"].waitForExistence(timeout: 15), "Use a fresh simulator without saved account credentials.")
        XCTAssertFalse(app.alerts.firstMatch.exists, "Startup must not present a Keychain or native-core error.")
    }

    private func select(_ title: String) {
        let tab = app.tabBars.buttons[title]
        if tab.exists {
            tab.tap()
        } else {
            // The iPad sidebar contains the same product labels as the phone tabs.
            let sidebarItem = app.staticTexts[title].firstMatch
            XCTAssertTrue(sidebarItem.waitForExistence(timeout: 5))
            sidebarItem.tap()
        }
    }

    private func reveal(_ element: XCUIElement, limit: Int = 8) {
        for _ in 0..<limit {
            if element.exists && element.isHittable { return }
            app.swipeUp()
        }
        XCTAssertTrue(element.exists && element.isHittable, "Expected control is not visible: \(element)")
    }

    private func screenshot(_ name: String) {
        let attachment = XCTAttachment(screenshot: app.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    func testPhoneTabsAndTabletSidebarReachUnsignedInPages() {
        XCTAssertTrue(app.staticTexts["登录 TermLink"].exists)
        screenshot("devices")
        select("CLI")
        XCTAssertTrue(app.staticTexts["打开 CLI 会话"].waitForExistence(timeout: 5))
        screenshot("cli-empty")
        select("AI")
        XCTAssertTrue(app.staticTexts["开始一段 AI 对话"].waitForExistence(timeout: 5))
        screenshot("ai-empty")
        app.buttons["新建"].tap()
        let create = app.buttons["创建对话"]
        XCTAssertTrue(create.waitForExistence(timeout: 5))
        XCTAssertFalse(create.isEnabled, "An unsigned-in device must not select a different or unavailable computer.")
        screenshot("ai-create-no-computer")
        app.buttons["取消"].tap()
        select("设置")
        XCTAssertTrue(app.textFields["HTTPS 服务地址"].waitForExistence(timeout: 5))
        XCTAssertEqual(app.textFields["HTTPS 服务地址"].value as? String, "https://ui-smoke.invalid")
        XCTAssertTrue(app.buttons["注册账号"].exists)
        XCTAssertTrue(app.buttons["登录帮助与找回密码"].exists)
        screenshot("settings-account")
        select("设备")
        XCTAssertTrue(app.staticTexts["登录 TermLink"].waitForExistence(timeout: 5))
        XCTAssertFalse(app.alerts.firstMatch.exists)
    }

    func testCoreIsLoadedAndBundledLicensesCanBeRead() {
        select("设置")
        let licenses = app.buttons["开源许可与致谢"]
        reveal(licenses)
        let loaded = app.descendants(matching: .any).matching(
            NSPredicate(format: "label CONTAINS %@ OR value == %@", "已接入", "已接入")
        ).firstMatch
        XCTAssertTrue(loaded.waitForExistence(timeout: 5), "Settings must report the actual linked terminal-core.")
        screenshot("settings-native-core")
        licenses.tap()
        for (filename, excerpt) in [
            ("terminal-core-LICENSE.txt", "GNU GENERAL PUBLIC LICENSE"),
            ("terminal-core-NOTICE.txt", "TermLink Terminal Core"),
            ("terminal-core-THIRD-PARTY-NOTICES.txt", "third_party/unicode")
        ] {
            let link = app.buttons[filename]
            reveal(link)
            link.tap()
            let navigation = app.navigationBars[filename]
            XCTAssertTrue(navigation.waitForExistence(timeout: 5))
            XCTAssertFalse(app.staticTexts["无法读取许可文件。"].exists)
            let body = app.scrollViews.staticTexts.matching(NSPredicate(format: "label CONTAINS %@", excerpt)).firstMatch
            XCTAssertTrue(body.waitForExistence(timeout: 5), "The selected license must show its bundled text.")
            screenshot(filename)
            navigation.buttons.element(boundBy: 0).tap()
        }
    }

    func testCustomShortcutPersistsAfterAppRelaunchAndSupportsUndo() {
        let label = "UI-" + String(UUID().uuidString.prefix(6))
        select("设置")
        let shortcuts = app.buttons["快捷键"]
        reveal(shortcuts)
        shortcuts.tap()
        app.buttons["添加快捷键"].tap()
        let name = app.textFields["显示名称（留空自动生成）"]
        XCTAssertTrue(name.waitForExistence(timeout: 5))
        name.tap(); name.typeText(label)
        let content = app.textViews["快捷输入内容"]
        content.tap(); content.typeText("echo simulator-test")
        let save = app.buttons["保存"]
        reveal(save); save.tap()
        XCTAssertTrue(app.staticTexts[label].waitForExistence(timeout: 5))
        screenshot("shortcut-saved")
        app.terminate(); app.launch()
        XCTAssertTrue(app.buttons["授权登录"].waitForExistence(timeout: 10))
        select("设置"); reveal(shortcuts); shortcuts.tap()
        XCTAssertTrue(app.staticTexts[label].waitForExistence(timeout: 5))
        screenshot("shortcut-restored")
        let row = app.cells.containing(.staticText, identifier: label).firstMatch
        row.swipeLeft()
        let delete = app.buttons["删除"].firstMatch
        if delete.exists { delete.tap() } else { app.buttons["Delete"].firstMatch.tap() }
        screenshot("shortcut-delete-presentation")
        let undo = app.buttons["撤销删除"].firstMatch
        XCTAssertTrue(undo.waitForExistence(timeout: 5)); undo.tap()
        XCTAssertTrue(app.staticTexts[label].waitForExistence(timeout: 5))
        screenshot("shortcut-undo")
        // Remove only this test's local fixture; do not rely on the platform's
        // cancel-button label, which differs between action sheets and popovers.
        row.swipeLeft()
        if delete.exists { delete.tap() } else { app.buttons["Delete"].firstMatch.tap() }
        app.terminate()
    }
}
