// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// Rendering state only. Live state continues to advance and persist while a
/// native, immutable history capture is displayed; history never supplies ACKs.
struct TerminalPresentation {
    private(set) var frame: TerminalFrame?
    private(set) var liveFrame: TerminalFrame?
    private(set) var frozen = false
    private(set) var offset = 0
    private(set) var availableRows = 0

    mutating func updateLive(_ value: TerminalFrame) {
        liveFrame = value
        if !frozen { frame = value }
    }
    mutating func begin(core: TLTerminalBridge) throws {
        guard !frozen else { return }
        do {
            let data = try core.beginHistory()
            let captured = try TerminalFrame.decode(data)
            if let liveFrame, captured.nextSequence != liveFrame.nextSequence {
                throw ClientError.protocolViolation("history capture revision")
            }
            frame = captured; frozen = true; offset = 0
            availableRows = Int(core.historyAvailableRows)
        } catch { core.endHistory(); throw error }
    }
    mutating func scroll(core: TLTerminalBridge, by rows: Int) throws {
        guard frozen else { throw ClientError.readOnly }
        let addition = offset.addingReportingOverflow(rows)
        let next = addition.overflow ? (rows > 0 ? availableRows : 0) : min(availableRows, max(0, addition.partialValue))
        try show(core: core, offset: next)
    }
    mutating func show(core: TLTerminalBridge, offset next: Int) throws {
        guard frozen, next >= 0, next <= availableRows else { throw ClientError.readOnly }
        let data = try core.renderHistoryFrame(atOffset: UInt32(next))
        let rendered = try TerminalFrame.decode(data)
        guard rendered.nextSequence == frame?.nextSequence else { throw ClientError.protocolViolation("history revision changed") }
        frame = rendered; offset = next
    }
    mutating func end(core: TLTerminalBridge?) {
        core?.endHistory(); frozen = false; offset = 0; availableRows = 0; frame = liveFrame
    }
    mutating func reset(core: TLTerminalBridge?) {
        end(core: core); liveFrame = nil; frame = nil
    }
}
