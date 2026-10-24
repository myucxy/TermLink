// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class TerminalWindowSizeTests: XCTestCase {
    func testResizeFailureMessagesMirrorAndroidReasons() {
        XCTAssertEqual(TerminalWindowSize.message(for: "RESIZE_UNSUPPORTED"), "请升级 Windows 客户端，并重新打开此终端会话。")
        XCTAssertEqual(TerminalWindowSize.message(for: "RESIZE_SERVER_UPGRADE_REQUIRED"), "服务端尚未支持窗口调整，请先升级服务端。")
        XCTAssertEqual(TerminalWindowSize.message(for: "WINDOW_RESIZE_UNSUPPORTED"), "Windows 未应用目标尺寸。请使用单标签、单窗格，退出全屏，并检查窗口大小限制。")
        XCTAssertEqual(TerminalWindowSize.message(for: "NO_DESKTOP_SIZE"), "没有可恢复的电脑尺寸，或电脑本地调整已替代该尺寸。")
        XCTAssertEqual(TerminalWindowSize.message(for: "CONTROL_LEASE_REJECTED"), "控制权限已变化，请确认当前窗口尺寸后再试。")
        XCTAssertEqual(TerminalWindowSize.message(for: "LOCAL_INPUT_PRIORITY"), "控制权限已变化，请确认当前窗口尺寸后再试。")
        XCTAssertEqual(TerminalWindowSize.message(for: "RESIZE_INTERRUPTED"), "控制权限已变化，请确认当前窗口尺寸后再试。")
        XCTAssertEqual(TerminalWindowSize.message(for: "RESIZE_TIMEOUT"), "未收到确认，请检查窗口实际尺寸及服务端版本后再试。")
        XCTAssertEqual(TerminalWindowSize.message(for: "RESIZE_BUSY"), "正在处理另一个窗口调整请求，请稍后再试。")
        XCTAssertEqual(TerminalWindowSize.message(for: "RESIZE_EXPIRED"), "请求已过期或尺寸无效，请检查两端系统时间及网络后重试。")
        XCTAssertEqual(TerminalWindowSize.message(for: "RESIZE_EXPIRED_OR_INVALID"), "请求已过期或尺寸无效，请检查两端系统时间及网络后重试。")
        XCTAssertEqual(TerminalWindowSize.message(for: "SOMETHING_NEW"), "无法调整此会话窗口，请检查连接后重试。")
        XCTAssertEqual(TerminalWindowSize.message(for: ""), "无法调整此会话窗口，请检查连接后重试。")
    }

    /// ResizePayload.restore is proto field 8; a plain resize must not serialize it.
    func testRestoreFlagUsesProtoFieldEight() throws {
        var restore = ProtoMessage(); restore.set(8, UInt64(1))
        XCTAssertEqual(try ProtoMessage(restore.encoded()).uint(8), 1)
        var fit = ProtoMessage(); fit.set(1, UInt64(80)); fit.set(2, UInt64(24))
        XCTAssertEqual(try ProtoMessage(fit.encoded()).uint(8), 0)
    }

    func testSnapshotEnvelopeRoundTripAndStorageName() throws {
        let envelope = PersistedTerminalSnapshot(version: 1, ack: 42, localInputEpoch: 7, snapshot: Data([1, 2, 3]))
        let decoded = try JSONDecoder().decode(PersistedTerminalSnapshot.self, from: JSONEncoder().encode(envelope))
        XCTAssertEqual(decoded.version, 1); XCTAssertEqual(decoded.ack, 42)
        XCTAssertEqual(decoded.localInputEpoch, 7); XCTAssertEqual(decoded.snapshot, Data([1, 2, 3]))
        let id = try XCTUnwrap(UUID(uuidString: "00000000-0000-0000-0000-000000000001"))
        XCTAssertEqual(PersistedTerminalSnapshot.storageName(id), "terminal:00000000-0000-0000-0000-000000000001")
    }
}
