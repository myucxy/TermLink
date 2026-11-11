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

    private func artifactSession(directory: String, agent: String = "Codex", pcID: UUID = UUID(), id: String = UUID().uuidString,
                                 pathStyle: String? = nil, hostPlatform: String = "") -> AIConversation {
        var capabilities: [String: JSONValue] = ["hostPlatform": .string(hostPlatform)]
        if let pathStyle { capabilities["pathStyle"] = .string(pathStyle) }
        return AIConversation(raw: .object(["id": .string(id), "agent": .string(agent), "directory": .string(directory),
                                           "capabilities": .object(capabilities)]), pcID: pcID)
    }

    func testArtifactLocationsUseHostSyntaxAndKeepTheSelectedSessionForEveryAgent() throws {
        for agent in ["Codex", "Claude Code", "ZCode", "Grok Build", "OpenCode", "future-agent"] {
            let session = artifactSession(directory: "/Users/test/项目 Work/", agent: agent)
            let location = try aiArtifactLocation(session: session, value: "/Users/test/项目 Work/输出 目录/嵌套/结果 文件.html")
            XCTAssertEqual(location.pcID, session.pcID); XCTAssertEqual(location.sessionID, session.id)
            XCTAssertEqual(location.directory, "/Users/test/项目 Work/输出 目录/嵌套")
            XCTAssertEqual(location.filePath, "/Users/test/项目 Work/输出 目录/嵌套/结果 文件.html")
            XCTAssertEqual(location, try aiArtifactLocation(session: session, value: "输出 目录/嵌套/结果 文件.html"))
            XCTAssertEqual(try aiArtifactLocation(session: session, value: "结果.html").directory, "/Users/test/项目 Work")

            let windows = artifactSession(directory: "C:\\项目 Work\\", agent: agent)
            let winLocation = try aiArtifactLocation(session: windows, value: "c:/项目 work/输出 目录/嵌套/结果 文件.html")
            XCTAssertEqual(winLocation.pcID, windows.pcID); XCTAssertEqual(winLocation.sessionID, windows.id)
            XCTAssertEqual(winLocation.directory, "C:\\项目 Work\\输出 目录\\嵌套")
            XCTAssertEqual(winLocation.filePath, "C:\\项目 Work\\输出 目录\\嵌套\\结果 文件.html")
        }
    }

    func testPOSIXArtifactPathsKeepCaseAndLiteralBackslashes() throws {
        let root = "/Users/me/Work", session = artifactSession(directory: "/Users/me/Work")
        for file in ["outputs\\draft/结果\\1.txt", "..\\literal.txt", "name:part.txt", "NUL.txt", "notes. "] {
            XCTAssertEqual(try aiArtifactRelativePath(directory: root, value: file), file)
            XCTAssertEqual(try aiArtifactRelativePath(directory: root, value: root + "/" + file), file)
            XCTAssertEqual(try aiArtifactRelativePath(directory: root, value: aiArtifactRelativePath(directory: root, value: root + "/" + file)), file)
            XCTAssertEqual(try aiArtifactLocation(session: session, value: file).filePath, root + "/" + file)
        }
        XCTAssertEqual(try aiArtifactLocation(session: session, value: "outputs\\draft/结果.txt").directory, root + "/outputs\\draft")
        let literalRoot = "/Users/me/项目\\草稿"
        XCTAssertEqual(try aiArtifactRelativePath(directory: literalRoot, value: literalRoot + "/out.txt"), "out.txt")
        for path in ["/users/me/Work/file", "/Users/me/work/file", "/Users/me/Workspace/file", "/etc/passwd",
                     "C:\\Users\\me\\Work\\file", "C:/Users/me/Work/file", "C:file", "\\\\server\\share\\file", "//server/share/file",
                     root + "/C:file", root + "/C:/file", root + "/\\\\server\\share\\file"] {
            XCTAssertThrowsError(try aiArtifactRelativePath(directory: root, value: path), "Accepted foreign path: \(path)")
        }
        XCTAssertThrowsError(try aiArtifactRelativePath(directory: "/work/é", value: "/work/e\u{301}/file"))
    }

    func testArtifactFilesystemRootsRemainAbsolute() throws {
        for root in ["D:\\", "D:/"] {
            let session = artifactSession(directory: root)
            let location = try aiArtifactLocation(session: session, value: "结果.txt")
            XCTAssertEqual(location.directory, "D:\\"); XCTAssertEqual(location.filePath, "D:\\结果.txt")
            XCTAssertEqual(location, try aiArtifactLocation(session: session, value: "d:/结果.txt"))
        }
        let rootFile = try aiArtifactLocation(session: artifactSession(directory: "/"), value: "/结果.txt")
        XCTAssertEqual(rootFile.directory, "/"); XCTAssertEqual(rootFile.filePath, "/结果.txt")
        XCTAssertEqual(try aiArtifactRelativePath(directory: "/", value: "/Users/me/输出 文件.txt"), "Users/me/输出 文件.txt")
        for path in ["C:/file", "C:\\file", "//server/share/file", "\\\\server\\share\\file"] {
            XCTAssertThrowsError(try aiArtifactRelativePath(directory: "/", value: path))
        }
    }

    func testUNCArtifactsStayInsideTheSelectedShare() throws {
        let session = artifactSession(directory: "\\\\Host\\Shared Dir\\项目\\")
        let location = try aiArtifactLocation(session: session, value: "//host/shared dir/项目/输出/文件.txt")
        XCTAssertEqual(location.pcID, session.pcID); XCTAssertEqual(location.sessionID, session.id)
        XCTAssertEqual(location.directory, "\\\\Host\\Shared Dir\\项目\\输出")
        XCTAssertEqual(location.filePath, "\\\\Host\\Shared Dir\\项目\\输出\\文件.txt")
        XCTAssertEqual(location, try aiArtifactLocation(session: session, value: "输出\\文件.txt"))
        XCTAssertEqual(try aiArtifactLocation(session: artifactSession(directory: "//Host/Shared Dir"), value: "file.txt").directory, "\\\\Host\\Shared Dir")
        for path in ["//other/Shared Dir/项目/file", "//Host/Shared Dir2/项目/file", "//Host/Shared Dir/项目2/file",
                     "\\\\?\\C:\\file", "\\\\.\\pipe\\file", "C:\\file", "\\file", "/file", "../file"] {
            XCTAssertThrowsError(try aiArtifactLocation(session: session, value: path))
        }
    }

    func testArtifactPathsRejectTraversalInvalidRootsAndWindowsAliases() {
        for root in ["/Users/me/Work", "C:\\Work", "//Host/Share/Work"] {
            for path in ["", ".", "..", "../secret", "out/../secret", "out/./file", "./file", "out//file", "out/", "bad\nfile", "bad\u{85}file", "bad\0file", String(repeating: "a", count: 2049)] {
                XCTAssertThrowsError(try aiArtifactRelativePath(directory: root, value: path), "Accepted \(path) under \(root)")
            }
            for path in [root, root + "/", root + "/../secret"] {
                XCTAssertThrowsError(try aiArtifactRelativePath(directory: root, value: path))
            }
        }
        for path in ["safe.txt:stream", "nested/file::$DATA", "..\\secret", "out\\..\\file", "out\\.\\file", "out\\\\file", "C:file",
                     "C:\\Other\\file", "D:\\Work\\file", "file.", "file ", "NUL", "nul.txt", "CON .txt", "COM1.log", "LPT².txt", "CONIN$",
                     "out/*", "out/?", "out/<", "out/>file", "out/\"", "out/pipe|file", "\\\\?\\C:\\Work\\file", "\\\\.\\pipe\\file"] {
            XCTAssertThrowsError(try aiArtifactRelativePath(directory: "C:\\Work", value: path), "Accepted Windows path: \(path)")
        }
        for root in ["", "relative", "D:", "D:work", "\\work", "/Users/me/../Work", "/Users//Work", "C:\\work\\..", "C:\\work.",
                     "\\\\server", "//server/../share", "//server/share:stream", "\\\\?\\C:\\Work", "\\\\.\\pipe", "/bad\nroot", "/" + String(repeating: "a", count: 4096)] {
            XCTAssertThrowsError(try aiArtifactRelativePath(directory: root, value: "file.txt"), "Accepted root: \(root)")
        }
    }

    func testArtifactSeparatorsRemainStructuralBesideUnicodeCombiningMarks() throws {
        let root = "/Users/me/Work", file = "输出/\u{301}结果.txt"
        XCTAssertEqual(try aiArtifactRelativePath(directory: root, value: root + "/" + file), file)
        XCTAssertEqual(try aiArtifactLocation(session: artifactSession(directory: root), value: file).directory, root + "/输出")
        XCTAssertEqual(try aiArtifactRelativePath(directory: "/\u{301}Work", value: "/\u{301}Work/file"), "file")
        XCTAssertEqual(try aiArtifactRelativePath(directory: "C:\\Work", value: "输出\\\u{301}结果.txt"), file)
        XCTAssertEqual(try aiArtifactLocation(session: artifactSession(directory: "C:\\Work"), value: file).filePath, "C:\\Work\\输出\\\u{301}结果.txt")
        for path in ["../\u{301}secret", "out/../\u{301}secret", "./\u{301}file", "/\u{301}outside", "//\u{301}host/share/file"] {
            XCTAssertThrowsError(try aiArtifactRelativePath(directory: root, value: path))
            XCTAssertThrowsError(try aiArtifactRelativePath(directory: "C:\\Work", value: path))
        }
        for path in ["safe:\u{301}stream", "CON.\u{301}txt", "out/\u{301}name:stream", "..\\\u{301}secret"] {
            XCTAssertThrowsError(try aiArtifactRelativePath(directory: "C:\\Work", value: path))
        }
        XCTAssertThrowsError(try aiArtifactRelativePath(directory: "C:\u{301}/Work", value: "file"))
        XCTAssertThrowsError(try aiArtifactRelativePath(directory: root, value: "\\\\\u{301}host\\share\\file"))
    }

    func testAdvertisedPathStyleControlsLocationsIndependentlyOfHostPlatformNames() throws {
        for platform in ["macos", "linux", "unknown"] {
            let session = artifactSession(directory: "/srv/项目 Work", pathStyle: "posix", hostPlatform: platform)
            let file = "输出\\草稿/结果 文件.txt"
            let location = try aiArtifactLocation(session: session, value: session.directory + "/" + file)
            XCTAssertEqual(location.pcID, session.pcID); XCTAssertEqual(location.sessionID, session.id)
            XCTAssertEqual(location.directory, session.directory + "/输出\\草稿")
            XCTAssertEqual(location.filePath, session.directory + "/" + file)
            XCTAssertEqual(try aiArtifactRelativePath(directory: session.directory, value: location.filePath, pathStyle: session.capabilities["pathStyle"].string), file)
            XCTAssertThrowsError(try aiArtifactLocation(session: session, value: "/SRV/项目 Work/输出.txt"))
        }
        for platform in ["windows", "unknown"] {
            let session = artifactSession(directory: "D:\\项目 Work", pathStyle: "windows", hostPlatform: platform)
            let location = try aiArtifactLocation(session: session, value: "d:/项目 work/输出 目录/结果.txt")
            XCTAssertEqual(location.directory, "D:\\项目 Work\\输出 目录")
            XCTAssertEqual(try aiArtifactRelativePath(directory: session.directory, value: location.filePath, pathStyle: "windows"), "输出 目录/结果.txt")
        }
    }

    func testAdvertisedPathStyleRejectsUnsupportedStylesAndMismatchedRoots() {
        let conflicts = [("windows", "/srv/work"), ("windows", "/"), ("posix", "D:\\Work"),
                         ("posix", "D:/"), ("posix", "//host/share"), ("posix", "\\\\host\\share")]
        for (style, root) in conflicts {
            XCTAssertThrowsError(try aiArtifactRelativePath(directory: root, value: "file.txt", pathStyle: style))
            XCTAssertThrowsError(try aiArtifactLocation(session: artifactSession(directory: root, pathStyle: style), value: "file.txt"))
        }
        for style in ["unsupported", "unknown", "macos", "linux", "plan9", "Windows", "POSIX", " "] {
            for root in ["D:\\Work", "/srv/work", "//host/share"] {
                let session = artifactSession(directory: root, pathStyle: style, hostPlatform: "unknown")
                XCTAssertThrowsError(try aiArtifactRelativePath(directory: root, value: "file.txt", pathStyle: style))
                XCTAssertThrowsError(try aiArtifactLocation(session: session, value: "file.txt"))
            }
        }
    }

    func testMissingPathStyleFallsBackOnlyToTheActualSessionDirectory() throws {
        for platform in ["", "unknown", "macos", "linux", "windows"] {
            for style in [nil, ""] as [String?] {
                let posix = artifactSession(directory: "/srv/work", pathStyle: style, hostPlatform: platform)
                XCTAssertEqual(try aiArtifactLocation(session: posix, value: "结果.txt").filePath, "/srv/work/结果.txt")
                let windows = artifactSession(directory: "E:\\Work", pathStyle: style, hostPlatform: platform)
                XCTAssertEqual(try aiArtifactLocation(session: windows, value: "结果.txt").filePath, "E:\\Work\\结果.txt")
                XCTAssertThrowsError(try aiArtifactLocation(session: artifactSession(directory: "relative", pathStyle: style, hostPlatform: platform), value: "结果.txt"))
            }
        }
        for root in ["/", "/srv/work", "D:\\", "D:\\Work", "//host/share"] {
            XCTAssertEqual(try aiArtifactRelativePath(directory: root, value: "out/结果.txt"),
                           try aiArtifactRelativePath(directory: root, value: "out/结果.txt", pathStyle: ""))
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


final class AIRefreshFenceTests: XCTestCase {
    func testMutationAndRecoveredReceiptRejectEarlierPollsPerComputer() {
        var fence=AIRefreshFence();let first=UUID(),second=UUID()
        let poll=fence.version(first),other=fence.version(second)
        fence.invalidate(first)
        XCTAssertFalse(fence.accepts(first,version:poll))
        XCTAssertTrue(fence.accepts(second,version:other))
        let during=fence.version(first)
        fence.invalidate(first)
        XCTAssertFalse(fence.accepts(first,version:during))
        XCTAssertTrue(fence.accepts(first,version:fence.version(first)))
    }
}
