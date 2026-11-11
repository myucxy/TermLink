// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class TerminalInputResultTests: XCTestCase {
    private let inputID=UUID(uuidString:"00112233-4455-6677-8899-aabbccddeeff")!
    private func core() throws -> TLTerminalBridge {
        XCTAssertTrue(TLTerminalBridge.available)
        return try XCTUnwrap(TLTerminalBridge(columns:40,rows:6,sequence:1))
    }
    private func result(accepted:Bool,reason:String="") -> ProtoMessage {
        var payload=ProtoMessage();payload.set(1,inputID.bytes);if accepted{payload.set(2,UInt64(1))};if !reason.isEmpty{payload.set(3,reason)};return payload
    }
    func testAcceptedInputConsumesSourceSequenceBeforeSubsequentOutputAndSnapshotRestore() throws {
        let terminal=try core()
        try terminal.applyData(Data("before".utf8),kind:1,sequence:1,columns:0,rows:0)
        let receipt=result(accepted:true)
        XCTAssertEqual(receipt.encoded(),Data([0x0a,0x10])+inputID.bytes+Data([0x10,0x01]),"Shared protobuf InputResultPayload fields 1/2 must match the host.")
        XCTAssertNil(try TerminalInputResult.apply(type:.inputAccepted,payload:receipt,sequence:2,to:terminal))
        let committed=try XCTUnwrap(terminal.exportSnapshot())
        let restored=try core();try restored.importSnapshot(committed)
        XCTAssertEqual(try TerminalFrame.decode(restored.renderFrame()).nextSequence,3)
        try restored.applyData(Data(" after".utf8),kind:1,sequence:3,columns:0,rows:0)
        let frame=try TerminalFrame.decode(restored.renderFrame())
        XCTAssertEqual(frame.nextSequence,4)
        XCTAssertTrue(frame.cells.map(\.text).joined().contains("before after"))
    }
    func testRejectedInputAlsoAdvancesNativeStateAndReturnsRevocationReason() throws {
        let terminal=try core(),receipt=result(accepted:false,reason:"LEASE_INVALID")
        XCTAssertEqual(receipt.encoded(),Data([0x0a,0x10])+inputID.bytes+Data([0x1a,0x0d])+Data("LEASE_INVALID".utf8))
        XCTAssertEqual(try TerminalInputResult.apply(type:.inputRejected,payload:receipt,sequence:1,to:terminal),"LEASE_INVALID")
        let snapshot=try XCTUnwrap(terminal.exportSnapshot()),restored=try core();try restored.importSnapshot(snapshot)
        try restored.applyData(Data("still readable".utf8),kind:1,sequence:2,columns:0,rows:0)
        XCTAssertEqual(try TerminalFrame.decode(restored.renderFrame()).nextSequence,3)
        XCTAssertThrowsError(try TerminalInputResult.apply(type:.inputRejected,payload:receipt,sequence:1,to:restored),"An old receipt cannot consume another source sequence.")
    }
    func testMalformedInputReceiptCannotAdvanceCoreOrGrantPermission() throws {
        var missing=ProtoMessage();missing.set(2,UInt64(1))
        var duplicate=result(accepted:true);duplicate.add(2,Data())
        var zero=result(accepted:true);zero.set(1,Data(repeating:0,count:16))
        var wrongWire=result(accepted:false);wrongWire.set(2,Data([0]))
        for (type,payload) in [(WireType.inputAccepted,missing),(.inputAccepted,zero),(.inputAccepted,result(accepted:false)),
                               (.inputRejected,result(accepted:true)),(.inputRejected,result(accepted:false,reason:"bad\ncontrol")),(.output,result(accepted:true)),(.inputAccepted,duplicate),(.inputRejected,wrongWire)] {
            let terminal=try core()
            XCTAssertThrowsError(try TerminalInputResult.apply(type:type,payload:payload,sequence:1,to:terminal))
            XCTAssertEqual(try TerminalFrame.decode(terminal.renderFrame()).nextSequence,1)
        }
    }
}
