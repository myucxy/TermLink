// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

@MainActor final class CLICreationRecoveryTests: XCTestCase {
    func testLostPOSTResponsePersistsBeforeSendAndRecoversSameKeyAfterReentry() async throws {
        let f=Fixture();f.dropFirst=true;let store=f.store()
        do{_=try await store.create(pc:f.pc,path:f.path,commandID:"zsh");XCTFail("response should be lost")}catch{}
        let saved=try XCTUnwrap(store.request);XCTAssertNil(saved.operationID);XCTAssertEqual(f.postCount,1)
        XCTAssertTrue(f.savedBeforeSend);XCTAssertEqual(f.creations,1)
        let recovered=f.store();recovered.show(pc:f.pc)
        XCTAssertEqual(recovered.request?.idempotency,saved.idempotency)
        XCTAssertEqual(recovered.request?.path,f.path)
        let session=try await recovered.recover(pc:f.pc)
        XCTAssertEqual(session,f.session);XCTAssertEqual(f.creations,1);XCTAssertEqual(f.postKeys,[saved.idempotency,saved.idempotency])
        XCTAssertEqual(f.postBodies[0],f.postBodies[1]);XCTAssertEqual(recovered.recent.first?.path,f.path)
    }
    func testProcessingRecoveryUsesOnlyGETAndNeverStartsAnotherShell() async throws {
        let f=Fixture();f.ready=false;let store=f.store()
        let created=try await store.create(pc:f.pc,path:f.path,commandID:"zsh");XCTAssertNil(created);XCTAssertEqual(store.request?.status,"PROCESSING")
        let fresh=f.store();fresh.show(pc:f.pc);f.ready=true
        let recovered=try await fresh.recover(pc:f.pc);XCTAssertEqual(recovered,f.session);XCTAssertEqual(f.postCount,1);XCTAssertEqual(f.creations,1)
        XCTAssertTrue(f.calls.dropFirst().allSatisfy{$0=="GET"})
    }
    func testPendingRequestCannotBeReplacedOrClearedByNewDirectory() async throws {
        let f=Fixture();f.ready=false;let store=f.store();_=try await store.create(pc:f.pc,path:f.path,commandID:"zsh")
        do{_=try await store.create(pc:f.pc,path:"/other",commandID:"zsh");XCTFail("duplicate") }catch{}
        XCTAssertThrowsError(try store.clearCompleted(pc:f.pc));XCTAssertEqual(f.postCount,1);XCTAssertEqual(store.request?.path,f.path)
    }
    func testPersistenceFailureSendsNothing() async throws {
        let f=Fixture();f.failSave=true;let store=f.store()
        do{_=try await store.create(pc:f.pc,path:f.path,commandID:"zsh");XCTFail("save must fail")}catch{}
        XCTAssertTrue(f.calls.isEmpty)
    }
    func testExpiredOrBackwardClockNeverReplaysUnknownPOST() async throws {
        for delta in [23.0*3600,24.0*3600, -1.0] {
            let f=Fixture();f.dropFirst=true;let store=f.store()
            do{_=try await store.create(pc:f.pc,path:f.path,commandID:"zsh")}catch{}
            f.time=f.time.addingTimeInterval(delta)
            do{_=try await store.recover(pc:f.pc);XCTFail("unsafe replay")}catch{}
            XCTAssertEqual(f.postCount,1);XCTAssertTrue(store.pending)
        }
    }
    func testKnownOperationRemainsGETAfterTwentyFourHours() async throws {
        let f=Fixture();f.ready=false;let store=f.store();_=try await store.create(pc:f.pc,path:f.path,commandID:"zsh")
        f.time=f.time.addingTimeInterval(48*3600);f.ready=true
        let recovered=try await store.recover(pc:f.pc);XCTAssertEqual(recovered,f.session);XCTAssertEqual(f.postCount,1)
    }
    func testAuthorityChangeNeverRebindsPendingRequestOrRecentDirectory() async throws {
        for change in 0..<4 {
            let f=Fixture();f.dropFirst=true;let store=f.store()
            do{_=try await store.create(pc:f.pc,path:f.path,commandID:"zsh")}catch{}
            let old=f.binding
            switch change {
            case 0:f.binding = .init(scope:old.scope,pcID:old.pcID,pcDeviceID:UUID(),requesterID:old.requesterID,keyEpoch:old.keyEpoch,platform:old.platform)
            case 1:f.binding = .init(scope:old.scope,pcID:old.pcID,pcDeviceID:old.pcDeviceID,requesterID:old.requesterID,keyEpoch:old.keyEpoch+1,platform:old.platform)
            case 2:f.binding = .init(scope:old.scope,pcID:old.pcID,pcDeviceID:old.pcDeviceID,requesterID:UUID(),keyEpoch:old.keyEpoch,platform:old.platform)
            default:f.allowed=false
            }
            do{_=try await store.recover(pc:f.pc);XCTFail("authority rebound")}catch{}
            XCTAssertEqual(f.postCount,1);XCTAssertEqual(store.request?.binding,old)
        }
    }
    func testAccountScopeIsolatedAndCorruptRecordFailsClosed() async throws {
        let f=Fixture();f.ready=false;let store=f.store();_=try await store.create(pc:f.pc,path:f.path,commandID:"zsh")
        let old=f.binding;f.binding = .init(scope:"other-account",pcID:old.pcID,pcDeviceID:old.pcDeviceID,requesterID:old.requesterID,keyEpoch:old.keyEpoch,platform:old.platform)
        store.show(pc:f.pc);XCTAssertNil(store.request);XCTAssertTrue(store.recent.isEmpty)
        f.corruptLoad=true;let next=f.store();next.show(pc:f.pc)
        do{_=try await next.create(pc:f.pc,path:f.path,commandID:"zsh");XCTFail("corrupt storage allowed new request")}catch{}
        XCTAssertEqual(f.postCount,1)
    }
    func testMismatchedOperationReceiptKeepsOriginalUnconfirmed() async throws {
        let f=Fixture();f.mutateResponse={value in var root=value.object;var op=value["file_operation"].object;op["path"] = .string("/foreign");root["file_operation"] = .object(op);return .object(root)}
        let store=f.store();do{_=try await store.create(pc:f.pc,path:f.path,commandID:"zsh");XCTFail("wrong receipt")}catch{}
        XCTAssertNil(store.request?.operationID);XCTAssertEqual(store.request?.path,f.path);XCTAssertTrue(store.pending)
    }
    func testInProgressAndNetworkFailureKeepSameOriginalKey() async throws {
        let f=Fixture();f.inProgress=true;let store=f.store()
        do{_=try await store.create(pc:f.pc,path:f.path,commandID:"zsh")}catch{}
        let id=store.request?.idempotency;f.inProgress=false
        _=try await store.recover(pc:f.pc)
        XCTAssertEqual(f.postKeys,[id!,id!]);XCTAssertEqual(f.creations,1)
    }
    func testNoGuessedDirectoryAndStrictAbsolutePaths() throws {
        for bad in ["", "~", "~/work", "relative", " /home", "/safe\0bad", "/line\nnew"] {XCTAssertThrowsError(try CLIPath.validate(bad,platform:"macos"))}
        for good in ["/", "/Users/fixture/项目", "/path with spaces "] {XCTAssertNoThrow(try CLIPath.validate(good,platform:"macos"))}
        XCTAssertNoThrow(try CLIPath.validate("",platform:"macos",allowRoots:true))
        XCTAssertNoThrow(try CLIPath.validate("C:\\Work",platform:"windows"));XCTAssertNoThrow(try CLIPath.validate("\\\\host\\share",platform:"windows"))
        XCTAssertThrowsError(try CLIPath.validate("C:relative",platform:"windows"))
    }
    func testDirectoryPagesFilterFilesRetainPagingAndRejectMalformedOffsets() async throws {
        let f=Fixture();let first=try CLIDirectoryPage.decode(.object(["result":.object(["current_path":.string("/work"),"parent_path":.string("/"),"entries":.array([entry("/work/file",kind:"file"),entry("/work/sub",kind:"directory")]),"next_offset":.number(2)])]),platform:"macos",offset:0)
        XCTAssertEqual(first.entries.map(\.id),["/work/sub"]);XCTAssertEqual(first.nextOffset,2)
        var seen:[String]=[];let browser=CLIDirectoryBrowser{path,offset,query in
            seen.append("\(path):\(offset):\(query)");return offset==0 ? first:.init(path:"/work",parent:"/",entries:[],nextOffset:nil)
        }
        browser.query="sub";await browser.browse("/work");browser.query="edited";await browser.browse("/work",more:true)
        XCTAssertEqual(seen,["/work:0:sub","/work:2:sub"]);XCTAssertEqual(browser.page?.entries.count,1)
        XCTAssertNil(f.store().request)
        XCTAssertThrowsError(try CLIDirectoryPage.decode(.object(["result":.object(["current_path":.string("/"),"entries":.array([]),"next_offset":.number(0)])]),platform:"macos",offset:0))
    }
    func testRecentDirectoryDoesNotCarryAcrossPCDeviceOrKeyEpoch() async throws {
        let f=Fixture();let store=f.store();_=try await store.create(pc:f.pc,path:f.path,commandID:"zsh")
        XCTAssertEqual(store.recent.first?.path,f.path)
        let old=f.binding;f.binding = .init(scope:old.scope,pcID:old.pcID,pcDeviceID:old.pcDeviceID,requesterID:old.requesterID,keyEpoch:8,platform:old.platform)
        store.show(pc:f.pc);XCTAssertTrue(store.recent.isEmpty)
    }
    func testResetDuringAwaitedResponseCannotPublishOrOpenOldAccount() async throws {
        let f=Fixture();var release:CheckedContinuation<Void,Never>?
        f.beforeReply={await withCheckedContinuation{release=$0}}
        let store=f.store();let task=Task{try await store.create(pc:f.pc,path:f.path,commandID:"zsh")}
        for _ in 0..<100 where release==nil{await Task.yield()}
        XCTAssertNotNil(release);store.reset();release?.resume()
        do{_=try await task.value;XCTFail("old account result published")}catch{}
        XCTAssertNil(store.request);XCTAssertFalse(store.busy);XCTAssertEqual(f.creations,1)
        let restored=f.store();restored.show(pc:f.pc);XCTAssertNil(restored.request?.operationID)
    }
    func testConcurrentClicksShareNoSecondCreation() async throws {
        let f=Fixture();var release:CheckedContinuation<Void,Never>?
        f.beforeReply={await withCheckedContinuation{release=$0}}
        let store=f.store();let task=Task{try await store.create(pc:f.pc,path:f.path,commandID:"zsh")}
        for _ in 0..<100 where release==nil{await Task.yield()}
        do{_=try await store.create(pc:f.pc,path:f.path,commandID:"zsh");XCTFail("second create")}catch{}
        do{_=try await store.recover(pc:f.pc);XCTFail("parallel recovery")}catch{}
        release?.resume();_=try await task.value;XCTAssertEqual(f.postCount,1);XCTAssertEqual(f.creations,1)
    }
    private func entry(_ path:String,kind:String)->JSONValue{.object(["path":.string(path),"name":.string(path.split(separator:"/").last.map(String.init) ?? "/"),"kind":.string(kind)])}

    @MainActor private final class Fixture {
        let pc=UUID(),device=UUID(),requester=UUID(),operation=UUID(),session=UUID(),path="/Users/fixture/项目"
        lazy var binding=CLICreationBinding(scope:"https://fixture.invalid\nfixture-account",pcID:pc,pcDeviceID:device,requesterID:requester,keyEpoch:7,platform:"macos")
        var time=Date(timeIntervalSince1970:1_800_000_000),allowed=true,ready=true,dropFirst=false,inProgress=false,failSave=false,corruptLoad=false,savedBeforeSend=false
        var archives:[String:CLICreationArchive]=[:],calls:[String]=[],postKeys:[UUID]=[],postBodies:[JSONValue]=[]
        var operations:[UUID:UUID]=[:],mutateResponse:((JSONValue)->JSONValue)?
        var beforeReply:(() async ->Void)?
        var postCount:Int{postKeys.count};var creations:Int{operations.count}
        var commands:[JSONValue]{[.object(["id":.string("zsh"),"display_name":.string("Z shell"),"source":.string("detected")])]}
        func store()->CLICreationStore {
            CLICreationStore(context:{[unowned self] id in guard allowed,id==pc else{throw ClientError.readOnly};return .init(binding:binding,commands:commands,canBrowse:true)},
                load:{[unowned self] scope,name in if corruptLoad{throw ClientError.invalidResponse("fixture archive")};return archives[scope+name]},
                save:{[unowned self] archive,scope,name in if failSave{throw ClientError.unavailable("fixture storage failed")};archives[scope+name]=archive},
                send:{[unowned self] route,method,body,key in
                    calls.append(method)
                    if method=="POST" {
                        let key=try XCTUnwrap(key);postKeys.append(key);postBodies.append(body)
                        savedBeforeSend=archives.values.contains{$0.request?.idempotency==key}
                        if inProgress{throw APIError(status:409,code:"IDEMPOTENCY_IN_PROGRESS",traceID:"")}
                        operations[key]=operations[key] ?? operation
                        if dropFirst && postCount==1{throw URLError(.networkConnectionLost)}
                    }else{XCTAssertTrue(route.hasSuffix(operation.uuidString));XCTAssertEqual(body,.null);XCTAssertNil(key)}
                    if let beforeReply{await beforeReply()}
                    var value:JSONValue = .object(["file_operation":.object(["id":.string(operation.uuidString),"pc_id":.string(pc.uuidString),"requester_device_id":.string(requester.uuidString),"operation":.string("OPEN_TERMINAL"),"path":.string(path),"terminal_command_id":.string("zsh"),"status":.string(ready ? "SUCCEEDED":"PROCESSING"),"result":ready ? .object(["session_id":.string(session.uuidString)]):.null])])
                    if let mutateResponse{value=mutateResponse(value)};return value
                },now:{[unowned self] in time},pollCount:0,sleep:{})
        }
    }
}
