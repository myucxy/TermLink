// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class FileTransferReadinessTests: XCTestCase {
    private let requester=UUID()
    private func record(zip:Bool=true)->DownloadRecord {
        .init(id:UUID(),pcID:UUID(),operationID:UUID(),path:"/fixture/folder",filename:"folder.zip",directoryZIP:zip,expectedBytes:0,received:0,status:"PREPARING",digest:Data())
    }
    private func operation(_ record:DownloadRecord,status:String="PROCESSING",ready:Bool=true)->JSONValue {
        var values:[String:JSONValue]=["id":.string(record.operationID.uuidString),"pc_id":.string(record.pcID.uuidString),
            "requester_device_id":.string(requester.uuidString),"operation":.string("DOWNLOAD"),"path":.string(record.path),"status":.string(status)]
        var response:[String:JSONValue] = ["file_operation":.object(values)]
        if ready { response["transfer"] = .object(["protocol":.string("REALTIME_V2"),"transfer_id":.string(record.operationID.uuidString),"filename":.string("folder.zip"),"size_bytes":.number(0),
            "sha256":.string(Data(repeating:7,count:32).base64URL),"directory_zip":.bool(record.directoryZIP)]) }
        return .object(response)
    }
    func testPendingAndPreparingZIPWaitForPublishedTransferDescriptor() throws {
        let r=record()
        for status in ["PENDING","PROCESSING"] { XCTAssertNil(try FileTransferDescriptor.prepared(operation(r,status:status,ready:false),record:r,requester:requester)) }
        let prepared=try XCTUnwrap(FileTransferDescriptor.prepared(operation(r),record:r,requester:requester))
        XCTAssertEqual(prepared.size,0);XCTAssertEqual(prepared.digest.count,32);XCTAssertEqual(prepared.filename,"folder.zip")
    }
    func testTerminalStatusNeverStartsOrRetriesAFileSocket() {
        let r=record()
        for status in ["FAILED","EXPIRED","CANCELLED","SUCCEEDED","UNKNOWN"] {
            XCTAssertThrowsError(try FileTransferDescriptor.prepared(operation(r,status:status),record:r,requester:requester))
        }
    }
    func testMismatchedIdentityDescriptorAndResumeContentAreRejected() {
        let r=record()
        let mutations:[String:JSONValue]=["id":.string(UUID().uuidString),"pc_id":.string(UUID().uuidString),
            "requester_device_id":.string(UUID().uuidString),"operation":.string("LIST"),"path":.string("/other"),
            "protocol":.string("OTHER"),"transfer_id":.string(UUID().uuidString),"filename":.string("../escape"),"size_bytes":.number(-1),
            "sha256":.string("bad"),"directory_zip":.bool(false)]
        for (key,value) in mutations {
            var changed=operation(r).object
            let group=["id","pc_id","requester_device_id","operation","path"].contains(key) ? "file_operation":"transfer"
            var nested=changed[group]!.object;nested[key]=value;changed[group] = .object(nested)
            XCTAssertThrowsError(try FileTransferDescriptor.prepared(.object(changed),record:r,requester:requester),key)
        }
        var resumed=r;resumed.digest=Data(repeating:9,count:32)
        XCTAssertThrowsError(try FileTransferDescriptor.prepared(operation(r),record:resumed,requester:requester))
    }
}
