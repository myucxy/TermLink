// SPDX-License-Identifier: GPL-3.0-only
import XCTest

/// Fresh unsigned-in simulator only. The tab fixture uses presentation data.
final class CLISessionTabsUITests: XCTestCase {
    private var app: XCUIApplication!
    override func setUpWithError() throws {
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments = ["-AppleLanguages", "(zh-Hans)", "-AppleLocale", "zh_CN", "-server", "https://ui-smoke.invalid", "--cli-session-tabs-ui-fixture"]
        app.launch()
        XCTAssertTrue(app.buttons["授权登录"].waitForExistence(timeout: 15), "Use an unsigned-in simulator.")
    }
    override func tearDownWithError() throws { app?.terminate() }

    func testDeviceTitleAndActionsShareNavigationBar() {
        let bar = app.navigationBars["你的终端"]
        XCTAssertTrue(bar.waitForExistence(timeout: 5))
        let history = bar.buttons["devices-history"]
        let downloads = bar.buttons["devices-downloads"]
        XCTAssertTrue(history.isHittable && downloads.isHittable)
        XCTAssertLessThan(bar.frame.height, 90, "Device title must use the inline navigation row.")
        XCTAssertEqual(history.frame.midY, downloads.frame.midY, accuracy: 3)
        capture("devices-inline-title-actions")
        history.tap()
        XCTAssertTrue(app.navigationBars["活动"].waitForExistence(timeout: 5))
        // Activity's existing sheet has no explicit close button. Relaunch
        // this unsigned-in fixture rather than depend on a system sheet gesture.
        app.terminate(); app.launch()
        XCTAssertTrue(app.buttons["授权登录"].waitForExistence(timeout: 15))
        let reopenedDownloads = app.navigationBars["你的终端"].buttons["devices-downloads"]
        XCTAssertTrue(reopenedDownloads.isHittable)
        reopenedDownloads.tap()
        XCTAssertTrue(app.navigationBars["下载"].waitForExistence(timeout: 5))
        app.buttons["关闭"].firstMatch.tap()
        XCTAssertTrue(bar.waitForExistence(timeout: 5))
    }

    func testCompactTabsSelectionScrollAndLocalClose() {
        app.buttons["CLI 标签隔离测试"].tap()
        let lastID = "11111111-1111-4111-8111-000000000008"
        let strip = app.scrollViews["cli-session-tabs"]
        XCTAssertTrue(strip.waitForExistence(timeout: 5))
        app.buttons["选择最后一个会话"].tap()
        let last = app.buttons["cli-session-tab-" + lastID]
        XCTAssertTrue(last.waitForExistence(timeout: 5))
        XCTAssertTrue(last.isHittable, "Selecting a distant tab must scroll it into view.")
        XCTAssertTrue(last.isSelected)
        XCTAssertEqual(app.staticTexts["cli-tabs-fixture-selected"].label, "Windows 工作站 · 会话 8")
        capture("cli-tabs-selected-last")
        app.buttons["cli-session-close-" + lastID].tap()
        XCTAssertFalse(last.exists)
        XCTAssertEqual(app.staticTexts["cli-tabs-fixture-closed"].label, "已关闭视图：1")
        XCTAssertEqual(app.staticTexts["cli-tabs-fixture-selected"].label, "Mac 开发电脑 · 会话 1")
        capture("cli-tabs-local-close")
    }

    private func capture(_ name: String) {
        let attachment = XCTAttachment(screenshot: app.screenshot())
        attachment.name = name; attachment.lifetime = .keepAlways; add(attachment)
    }
}
