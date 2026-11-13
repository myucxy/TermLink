// SPDX-License-Identifier: GPL-3.0-only
import XCTest

final class CLICreationUITests: XCTestCase {
    func testDirectorySelectionAndLostReceiptSurviveClosingSheetWithoutDuplicateCreate() throws {
        continueAfterFailure=false
        let app=XCUIApplication()
        app.launchArguments=["-AppleLanguages","(zh-Hans)","-AppleLocale","zh_CN","-server","https://cli-create-ui-fixture.invalid","--cli-create-ui-fixture"]
        app.launch()
        let entry=app.buttons["CLI 创建隔离测试"]
        XCTAssertTrue(entry.waitForExistence(timeout:15));entry.tap()
        app.buttons["新建测试 CLI"].tap()
        let create=app.buttons["cli-create-submit"]
        XCTAssertTrue(create.waitForExistence(timeout:5));XCTAssertFalse(create.isEnabled)
        app.buttons["浏览电脑目录"].tap()
        let directory=app.buttons["cli-directory-/fixture/项目"]
        XCTAssertTrue(directory.waitForExistence(timeout:5));directory.tap()
        app.buttons["使用此目录"].tap()
        XCTAssertTrue(app.staticTexts["cli-selected-directory"].waitForExistence(timeout:5))
        XCTAssertEqual(app.staticTexts["cli-selected-directory"].label,"/fixture/项目")
        XCTAssertTrue(create.isEnabled);create.tap()
        let recover=app.buttons["cli-recover-request"]
        reveal(recover,in:app)
        XCTAssertTrue(recover.exists);XCTAssertFalse(create.exists)
        XCTAssertFalse(app.descendants(matching:.any).matching(identifier:"cli-working-directory").firstMatch.isEnabled)
        app.navigationBars["新建 CLI 会话"].buttons["关闭"].tap()
        XCTAssertEqual(app.staticTexts["cli-fixture-created-count"].label,"创建次数：1")
        app.buttons["新建测试 CLI"].tap()
        reveal(recover,in:app);recover.tap()
        XCTAssertTrue(app.staticTexts["cli-fixture-opened"].waitForExistence(timeout:8))
        XCTAssertEqual(app.staticTexts["cli-fixture-created-count"].label,"创建次数：1")
        let screenshot=XCTAttachment(screenshot:app.screenshot());screenshot.name="cli-original-request-recovered";screenshot.lifetime = .keepAlways;add(screenshot)
        app.terminate()
    }
    private func reveal(_ element:XCUIElement,in app:XCUIApplication) {
        for _ in 0..<6 {if element.exists && element.isHittable{return};app.swipeUp()}
        XCTAssertTrue(element.exists && element.isHittable)
    }
}
