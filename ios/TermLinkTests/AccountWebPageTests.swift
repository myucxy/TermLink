// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class AccountWebPageTests: XCTestCase {
    func testAccountFlowsStayOnTheSelectedServiceAndUseExistingPortalRoutes() throws {
        let server = "https://example.test:8910/"
        XCTAssertEqual(try AccountWebPage.registration.url(server: server).absoluteString, "https://example.test:8910/register")
        XCTAssertEqual(try AccountWebPage.loginHelp.url(server: server).absoluteString, "https://example.test:8910/login")
        XCTAssertEqual(try AccountWebPage.security.url(server: server).absoluteString, "https://example.test:8910/account/identity")
        XCTAssertEqual(try AccountWebPage.privacy.url(server: server).absoluteString, "https://example.test:8910/account?section=privacy")
        for page in AccountWebPage.allCases {
            for invalid in ["http://example.test", "https://user:secret@example.test", "https://example.test/path", "https://example.test?token=secret"] {
                XCTAssertThrowsError(try page.url(server: invalid))
            }
        }
    }
}
