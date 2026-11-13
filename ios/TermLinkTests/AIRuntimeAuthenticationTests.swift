// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class AIRuntimeAuthenticationTests:XCTestCase {
    func testOnlyExactOfficialDevicePageAndBoundedUnexpiredCodeBecomeActionable() throws {
        let now=Date(timeIntervalSince1970:1_800_000_000)
        let formatter=ISO8601DateFormatter();formatter.formatOptions=[.withInternetDateTime,.withFractionalSeconds]
        var value:[String:JSONValue] = ["status":.string("pending"),"method":.string("device_code"),"verificationUrl":.string("https://auth.openai.com/codex/device"),"userCode":.string("ABCD-1234"),"expiresAt":.string(formatter.string(from:now.addingTimeInterval(60)))]
        XCTAssertEqual(AIRuntimeAuthentication.challenge(.object(value),now:now)?.1,"ABCD-1234")
        for url in ["https://auth.openai.com.evil.invalid/codex/device","https://auth.openai.com/codex/device?token=secret","http://auth.openai.com/codex/device","https://user@auth.openai.com/codex/device"] {
            value["verificationUrl"] = .string(url)
            XCTAssertNil(AIRuntimeAuthentication.challenge(.object(value),now:now))
        }
        value["verificationUrl"] = .string("https://auth.openai.com/codex/device")
        for code in ["",String(repeating:"A",count:33),"CODE\n1234","<script>","ＡＢＣＤ"] {
            value["userCode"] = .string(code);XCTAssertNil(AIRuntimeAuthentication.challenge(.object(value),now:now))
        }
        value["userCode"] = .string("ABCD-1234");value["expiresAt"] = .string(formatter.string(from:now))
        XCTAssertNil(AIRuntimeAuthentication.challenge(.object(value),now:now))
    }
    func testMutatingReceiptRequiresOriginalAgentAndNeverCarriesChallenge() throws {
        let valid:JSONValue = .object(["authentication":.object(["agentId":.string("codex"),"status":.string("pending")])])
        XCTAssertNoThrow(try AIRuntimeAuthentication.validateReceipt(action:"authentication.start",agent:"codex",response:valid))
        XCTAssertThrowsError(try AIRuntimeAuthentication.validateReceipt(action:"authentication.start",agent:"other",response:valid))
        XCTAssertThrowsError(try AIRuntimeAuthentication.validateReceipt(action:"authentication.cancel",agent:"codex",response:.object([:])))
        let stale:JSONValue = .object(["authentication":.object(["agentId":.string("codex"),"status":.string("pending"),"userCode":.string("ABCD-1234")])])
        XCTAssertThrowsError(try AIRuntimeAuthentication.validateReceipt(action:"authentication.start",agent:"codex",response:stale))
        XCTAssertNoThrow(try AIRuntimeAuthentication.validateReceipt(action:"authentication.start",agent:"codex",response:.object(["error":.string("RUNTIME_AUTH_UNAVAILABLE")])))
    }
    func testUnknownAndAuthenticatedStatusNeverClaimModelAccess() {
        XCTAssertTrue(AIRuntimeAuthentication.summary(.null).contains("不代表已经登录"))
        XCTAssertTrue(AIRuntimeAuthentication.summary(.object(["status":.string("authenticated")])).contains("模型权限仍需"))
        XCTAssertNil(AIRuntimeAuthentication.challenge(.object(["status":.string("authenticated")])))
    }
}
