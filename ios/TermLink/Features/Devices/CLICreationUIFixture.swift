// SPDX-License-Identifier: GPL-3.0-only
#if DEBUG
import SwiftUI

/// Explicit process-only UI test entry. This fixture has no API client,
/// credentials, filesystem writes or route to a real computer.
struct CLICreationUIFixture: View {
    @StateObject private var fixture = Model()
    @State private var showing = false
    var body: some View {
        VStack(spacing:16) {
            Text("隔离 CLI 界面测试")
            Button("新建测试 CLI"){showing=true}
            Text("创建次数：\(fixture.creations)").accessibilityIdentifier("cli-fixture-created-count")
            if fixture.opened {Text("已打开原会话").accessibilityIdentifier("cli-fixture-opened")}
        }.sheet(isPresented:$showing) {
            NavigationStack {CLICreationForm(pc:fixture.pc,store:fixture.store,directoryPage:fixture.page,opened:{_ in fixture.opened=true})}
        }
    }
    @MainActor private final class Model: ObservableObject {
        let pcID=UUID(), device=UUID(), requester=UUID(), operation=UUID(), session=UUID()
        @Published var creations=0
        @Published var opened=false
        var archive:CLICreationArchive?, key:UUID?, first=true
        lazy var pc: PC = try! PC(.object([
            "id": .string(pcID.uuidString), "device_id": .string(device.uuidString), "key_epoch": .number(1),
            "name": .string("测试 Mac"), "platform": .string("macos"), "online": .bool(true), "remote_routing_enabled": .bool(true),
            "capabilities": .array(["file_operations_v1","terminal_commands_v1","realtime_terminal_v1"].map(JSONValue.string)),
            "terminal_commands": .array([.object(["id": .string("zsh"), "display_name": .string("Z shell"), "source": .string("detected")])])
        ]))
        lazy var store=CLICreationStore(context:{[unowned self] id in
            guard id==pcID else{throw ClientError.readOnly};return .init(binding:.init(scope:"ui-fixture",pcID:pcID,pcDeviceID:device,requesterID:requester,keyEpoch:1,platform:"macos"),commands:pc.commands,canBrowse:true)
        },load:{[unowned self] _,_ in archive},save:{[unowned self] value,_,_ in archive=value},send:{[unowned self] _,method,body,id in
            if method=="POST" {
                if key==nil{key=id;creations+=1};guard key==id else{throw ClientError.protocolViolation("fixture duplicate")}
                if first{first=false;throw ClientError.unavailable("模拟回执丢失：原请求已经收到。")}
            }
            return .object(["file_operation":.object(["id":.string(operation.uuidString),"pc_id":.string(pcID.uuidString),"requester_device_id":.string(requester.uuidString),"operation":.string("OPEN_TERMINAL"),"path":.string(archive?.request?.path ?? ""),"terminal_command_id":.string("zsh"),"status":.string("SUCCEEDED"),"result":.object(["session_id":.string(session.uuidString)])])])
        },pollCount:0)
        func page(_ path:String,_ offset:Int64,_ query:String) async throws ->CLIDirectoryPage {
            if path.isEmpty{return .init(path:"",parent:nil,entries:[RemoteEntry(raw:.object(["name":.string("项目目录"),"path":.string("/fixture/项目"),"kind":.string("directory")]))],nextOffset:nil)}
            return .init(path:path,parent:"",entries:[],nextOffset:nil)
        }
    }
}
#endif
