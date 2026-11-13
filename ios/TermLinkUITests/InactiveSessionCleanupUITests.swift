// SPDX-License-Identifier: GPL-3.0-only
import XCTest

final class InactiveSessionCleanupUITests: XCTestCase {
    func testConfirmationCancelAndMixedReceiptsAreVisible() throws {
        let app = fixture()
        app.buttons["打开清理测试"].tap()
        let confirm = app.buttons["cleanup-confirm"]
        XCTAssertTrue(confirm.waitForExistence(timeout: 5))
        XCTAssertFalse(app.staticTexts["正在运行，禁止清理"].exists)
        XCTAssertTrue(app.staticTexts["已结束记录 1"].exists)
        confirm.tap(); app.buttons["取消"].tap()
        XCTAssertTrue(confirm.exists)
        confirm.tap(); app.buttons["确认清理"].tap()
        let counts = app.staticTexts["cleanup-counts"]
        XCTAssertTrue(counts.waitForExistence(timeout: 5))
        XCTAssertTrue(counts.label.contains("已清理 1")); XCTAssertTrue(counts.label.contains("跳过 1")); XCTAssertTrue(counts.label.contains("未确认 1"))
        XCTAssertEqual(confirm.label, "重试未确认记录")
        let screenshot = XCTAttachment(screenshot: app.screenshot()); screenshot.name = "cleanup-mixed-receipts"; screenshot.lifetime = .keepAlways; add(screenshot)
        app.buttons["关闭"].tap()
        XCTAssertEqual(app.staticTexts["cleanup-fixture-batches"].label, "已发送批次：1")
        XCTAssertEqual(app.staticTexts["cleanup-fixture-deleted"].label, "已确认清理：1")
        app.terminate()
    }
    func testStopWaitsForCurrentBatchAndDoesNotSendNextBatch() throws {
        let app = fixture(batch: true)
        app.buttons["打开清理测试"].tap()
        let confirm = app.buttons["cleanup-confirm"]
        XCTAssertTrue(confirm.waitForExistence(timeout: 5)); confirm.tap(); app.buttons["确认清理"].tap()
        let stop = app.buttons["cleanup-cancel"]
        XCTAssertTrue(stop.waitForExistence(timeout: 5)); stop.tap()
        XCTAssertFalse(app.buttons["关闭"].isEnabled)
        XCTAssertTrue(app.staticTexts["cleanup-status"].label.contains("当前批次"))
        let counts = app.staticTexts["cleanup-counts"]
        let complete = NSPredicate(format: "label CONTAINS %@", "已清理 64")
        expectation(for: complete, evaluatedWith: counts); waitForExpectations(timeout: 8)
        XCTAssertTrue(app.buttons["关闭"].isEnabled)
        app.buttons["关闭"].tap()
        XCTAssertEqual(app.staticTexts["cleanup-fixture-batches"].label, "已发送批次：1")
        XCTAssertEqual(app.staticTexts["cleanup-fixture-deleted"].label, "已确认清理：64")
        app.terminate()
    }
    private func fixture(batch: Bool = false) -> XCUIApplication {
        continueAfterFailure = false
        let app = XCUIApplication()
        app.launchArguments = ["-AppleLanguages", "(zh-Hans)", "-AppleLocale", "zh_CN", "-server", "https://cleanup-ui-fixture.invalid", "--session-cleanup-ui-fixture"]
        if batch { app.launchArguments.append("--cleanup-batch-fixture") }
        app.launch()
        let entry = app.buttons["会话清理隔离测试"]
        XCTAssertTrue(entry.waitForExistence(timeout: 15)); entry.tap()
        return app
    }
}
