// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class AIMessageContentTests: XCTestCase {
    func testEmptyOrWhitespaceTextWithoutFilesOrReferencesIsNotSendable() throws {
        for text in ["", " ", "\t\r\n", "\u{3000}\u{00a0}"] {
            XCTAssertFalse(try AIMessageContent.hasContent(text: text, attachmentCount: 0, fileReferences: []))
        }
    }

    func testTextAttachmentsAndComputerReferencesEachProvideMessageContent() throws {
        XCTAssertTrue(try AIMessageContent.hasContent(text: " text \n", attachmentCount: 0, fileReferences: []))
        XCTAssertTrue(try AIMessageContent.hasContent(text: "", attachmentCount: 1, fileReferences: []))
        XCTAssertTrue(try AIMessageContent.hasContent(text: "", attachmentCount: 0, fileReferences: ["C:\\work\\notes.txt"]))
        XCTAssertTrue(try AIMessageContent.hasContent(text: " \n", attachmentCount: 0, fileReferences: ["C:\\项目\\说明.md"]))
        XCTAssertTrue(try AIMessageContent.hasContent(text: "review", attachmentCount: 2,
            fileReferences: ["C:\\work\\a.swift", "C:\\work\\b.swift"]))
    }

    func testReferenceCountIsBoundedBeforeSendingEvenWhenTextOrFilesExist() throws {
        let maximum = (0..<5).map { "C:\\work\\file-\($0).txt" }
        XCTAssertTrue(try AIMessageContent.hasContent(text: "", attachmentCount: 0, fileReferences: maximum))
        XCTAssertThrowsError(try AIMessageContent.hasContent(text: "valid text", attachmentCount: 1,
            fileReferences: maximum + ["C:\\work\\extra.txt"])) { error in
            XCTAssertEqual(error as? ClientError, .limitExceeded)
        }
    }

    func testReferenceByteLimitUsesUTF8RatherThanCharacterCount() throws {
        let asciiMaximum = String(repeating: "a", count: 4096)
        let unicodeMaximum = String(repeating: "中", count: 1365) + "x"
        XCTAssertEqual(unicodeMaximum.utf8.count, 4096)
        for reference in [asciiMaximum, unicodeMaximum] {
            XCTAssertTrue(try AIMessageContent.hasContent(text: "", attachmentCount: 0, fileReferences: [reference]))
        }
        for reference in [asciiMaximum + "x", String(repeating: "中", count: 1366)] {
            XCTAssertThrowsError(try AIMessageContent.hasContent(text: "valid text", attachmentCount: 1,
                fileReferences: [reference])) { error in
                XCTAssertEqual(error as? ClientError, .limitExceeded)
            }
        }
    }

    func testEmptyWhitespaceAndNULReferencesCannotHideBehindOtherContent() {
        for reference in ["", " \t\r\n", "\u{3000}", "C:\\work\\bad\0.txt", "\0"] {
            XCTAssertThrowsError(try AIMessageContent.hasContent(text: "valid text", attachmentCount: 1,
                fileReferences: ["C:\\work\\valid.txt", reference])) { error in
                XCTAssertEqual(error as? ClientError, .invalidResponse("AI file reference"))
            }
        }
    }

    private func sendRequest(text: String = "sent draft", attachments: [String] = ["attachment-A"],
                             references: [String] = ["C:\\work\\A.txt"]) -> JSONValue {
        .object(["action": .string("send"), "text": .string(text),
                 "attachmentIds": .array(attachments.map(JSONValue.string)),
                 "fileReferences": .array(references.map(JSONValue.string))])
    }

    func testReceiptRemovesOnlySentAttachmentsAndReferencesAndPreservesConcurrentAdditions() {
        let remaining = AIMessageContent.remainingAfterAcknowledgement(request: sendRequest(), draft: "sent draft",
            attachmentIDs: ["attachment-A", "attachment-B", "attachment-C"],
            fileReferences: ["C:\\work\\A.txt", "C:\\work\\B.txt", "C:\\work\\C.txt"])
        XCTAssertEqual(remaining.draft, "")
        XCTAssertEqual(remaining.attachmentIDs, ["attachment-B", "attachment-C"])
        XCTAssertEqual(remaining.fileReferences, ["C:\\work\\B.txt", "C:\\work\\C.txt"])
    }

    func testReceiptClearsOnlyUnchangedDraftAndLeavesEditedOrMissingDraftUntouched() {
        let drafts: [String?] = ["sent draft", "newly edited draft", nil]
        for draft in drafts {
            let remaining = AIMessageContent.remainingAfterAcknowledgement(request: sendRequest(), draft: draft,
                attachmentIDs: [], fileReferences: [])
            XCTAssertEqual(remaining.draft, draft == "sent draft" ? "" : draft)
        }
    }

    func testReceiptHandlesDuplicateReferenceValuesWithoutRemovingOtherPaths() {
        let remaining = AIMessageContent.remainingAfterAcknowledgement(
            request: sendRequest(references: ["C:\\work\\A.txt", "C:\\work\\A.txt"]), draft: "new draft",
            attachmentIDs: ["attachment-A", "attachment-B"],
            fileReferences: ["C:\\work\\B.txt", "C:\\work\\A.txt", "C:\\work\\B.txt", "C:\\work\\A.txt"])
        XCTAssertEqual(remaining.fileReferences, ["C:\\work\\B.txt", "C:\\work\\B.txt"])
        XCTAssertEqual(remaining.draft, "new draft")
    }

    func testEmptySentFileListsDoNotClearNewlyPreparedAttachmentsOrReferences() {
        let remaining = AIMessageContent.remainingAfterAcknowledgement(
            request: sendRequest(attachments: [], references: []), draft: "sent draft",
            attachmentIDs: ["photo-finished-preparing-later"], fileReferences: ["C:\\work\\later.txt"])
        XCTAssertEqual(remaining.draft, "")
        XCTAssertEqual(remaining.attachmentIDs, ["photo-finished-preparing-later"])
        XCTAssertEqual(remaining.fileReferences, ["C:\\work\\later.txt"])
    }

    func testQueuedSendReceiptNeverClearsCurrentComposer() {
        for queueID in [JSONValue.string("queue-1"), .string(""), .null] {
            var request = sendRequest().object
            request["queueId"] = queueID
            assertReceiptLeavesComposerUnchanged(.object(request))
        }
    }

    func testUnknownOrIncompleteRequestReceiptsCannotDeleteComposerContent() {
        assertReceiptLeavesComposerUnchanged(.object([:]))
        assertReceiptLeavesComposerUnchanged(.null)
        var unknown = sendRequest().object
        unknown["action"] = .string("configure")
        assertReceiptLeavesComposerUnchanged(.object(unknown))
        for field in ["action", "text", "attachmentIds", "fileReferences"] {
            var incomplete = sendRequest().object
            incomplete.removeValue(forKey: field)
            assertReceiptLeavesComposerUnchanged(.object(incomplete))
        }
        for field in ["attachmentIds", "fileReferences"] {
            for invalid in [JSONValue.array([.number(1)]), .array([.string("")]), .string("invalid")] {
                var malformed = sendRequest().object
                malformed[field] = invalid
                assertReceiptLeavesComposerUnchanged(.object(malformed))
            }
        }
    }

    func testRepeatedReceiptIsIdempotentAndDoesNotDeleteLaterContent() {
        let request = sendRequest()
        let first = AIMessageContent.remainingAfterAcknowledgement(request: request, draft: "sent draft",
            attachmentIDs: ["attachment-A", "attachment-B"], fileReferences: ["C:\\work\\A.txt", "C:\\work\\B.txt"])
        let second = AIMessageContent.remainingAfterAcknowledgement(request: request, draft: first.draft,
            attachmentIDs: first.attachmentIDs, fileReferences: first.fileReferences)
        XCTAssertEqual(first, second)
    }

    private func assertReceiptLeavesComposerUnchanged(_ request: JSONValue, file: StaticString = #filePath, line: UInt = #line) {
        let remaining = AIMessageContent.remainingAfterAcknowledgement(request: request, draft: "sent draft",
            attachmentIDs: ["attachment-A", "attachment-B"], fileReferences: ["C:\\work\\A.txt", "C:\\work\\B.txt"])
        XCTAssertEqual(remaining.draft, "sent draft", file: file, line: line)
        XCTAssertEqual(remaining.attachmentIDs, ["attachment-A", "attachment-B"], file: file, line: line)
        XCTAssertEqual(remaining.fileReferences, ["C:\\work\\A.txt", "C:\\work\\B.txt"], file: file, line: line)
    }
}
