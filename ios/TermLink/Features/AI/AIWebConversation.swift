// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI
import WebKit

/// Host-advertised style is authoritative; only old hosts fall back to their actual directory syntax.
private struct AIArtifactRoot {
    let path: String
    let windows: Bool
    var prefix: String { path.utf16.last == 47 ? path : path + "/" }

    init(_ directory: String, pathStyle: String) throws {
        guard Self.validText(directory, maximum: 4096) else { throw ClientError.invalidResponse("artifact root") }
        let drive = Self.hasDrive(directory)
        let network = Self.hasNetworkRoot(directory)
        switch pathStyle {
        case "windows": windows = true
        case "posix": windows = false
        case "": windows = drive || network
        default: throw ClientError.invalidResponse("host path style")
        }
        guard windows ? (drive || network) : (!drive && !network) else { throw ClientError.invalidResponse("artifact root") }
        let normalized = windows ? directory.replacingOccurrences(of: "\\", with: "/", options: .literal) : directory
        var rootUnits = Array(normalized.utf16)
        while rootUnits.last == 47 { rootUnits.removeLast() }
        let root = String(decoding: rootUnits, as: UTF16.self)
        if drive {
            guard normalized.utf16.dropFirst(2).first == 47,
                  rootUnits.count == 2 || Self.validSegments(String(decoding: rootUnits.dropFirst(3), as: UTF16.self), windows: true) else {
                throw ClientError.invalidResponse("artifact root")
            }
            path = rootUnits.count == 2 ? root + "/" : root
        } else if windows {
            let share = String(decoding: rootUnits.dropFirst(2), as: UTF16.self)
            guard rootUnits.starts(with: [47, 47]), share.utf16.split(separator: 47).count >= 2,
                  Self.validSegments(share, windows: true) else { throw ClientError.invalidResponse("artifact root") }
            path = root
        } else {
            guard normalized.utf16.first == 47,
                  root.isEmpty || Self.validSegments(String(decoding: rootUnits.dropFirst(), as: UTF16.self), windows: false) else {
                throw ClientError.invalidResponse("artifact root")
            }
            path = root.isEmpty ? "/" : root
        }
    }

    func relative(_ value: String) throws -> String {
        guard Self.validText(value, maximum: 2048),
              windows || (!Self.hasDrive(value) && !Self.hasNetworkRoot(value)) else {
            throw ClientError.invalidResponse("artifact path")
        }
        let normalized = windows ? value.replacingOccurrences(of: "\\", with: "/", options: .literal) : value
        let options: NSString.CompareOptions = windows ? [.anchored, .literal, .caseInsensitive] : [.anchored, .literal]
        let match = (normalized as NSString).range(of: prefix, options: options)
        let relative = match.location == 0 ? (normalized as NSString).substring(from: NSMaxRange(match)) : normalized
        guard relative.utf16.first != 47, Self.validSegments(relative, windows: windows),
              windows || (!Self.hasDrive(relative) && !Self.hasNetworkRoot(relative)) else {
            throw ClientError.invalidResponse("artifact path")
        }
        return relative
    }

    func native(_ value: String) -> String { windows ? value.replacingOccurrences(of: "/", with: "\\", options: .literal) : value }

    private static func hasNetworkRoot(_ value: String) -> Bool {
        value.utf16.starts(with: [92, 92]) || value.utf16.starts(with: [47, 47])
    }

    private static func hasDrive(_ value: String) -> Bool {
        let start = Array(value.utf16.prefix(2))
        return start.count == 2 && ((65...90).contains(start[0]) || (97...122).contains(start[0])) && start[1] == 58
    }

    private static func validText(_ value: String, maximum: Int) -> Bool {
        !value.isEmpty && value.utf16.count <= maximum &&
            !value.unicodeScalars.contains { $0.value <= 31 || (127...159).contains($0.value) }
    }

    private static func validSegments(_ value: String, windows: Bool) -> Bool {
        // ASCII separators are code units: Swift Character splitting can hide "/" before a combining mark.
        value.utf16.split(separator: 47, omittingEmptySubsequences: false).allSatisfy { units in
            let part = String(decoding: units, as: UTF16.self)
            guard !part.isEmpty, part != ".", part != ".." else { return false }
            guard windows else { return true }
            var base = Array(units.prefix(while: { $0 != 46 }))
            while base.last == 32 { base.removeLast() }
            let device = String(decoding: base, as: UTF16.self).uppercased()
            let reserved = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"].contains(device) ||
                (device.count == 4 && (device.hasPrefix("COM") || device.hasPrefix("LPT")) && "123456789¹²³".contains(device.last!))
            return !units.contains { "<>:\"|?*".utf16.contains($0) } && units.last != 46 && units.last != 32 && !reserved
        }
    }
}

func aiArtifactRelativePath(directory: String, value: String, pathStyle: String = "") throws -> String {
    try AIArtifactRoot(directory, pathStyle: pathStyle).relative(value)
}

struct AIArtifactLocation: Equatable {
    let pcID: UUID
    let sessionID: String
    let directory: String
    let filePath: String
}

func aiArtifactLocation(session: AIConversation, value: String) throws -> AIArtifactLocation {
    let root = try AIArtifactRoot(session.directory, pathStyle: session.capabilities["pathStyle"].string), relative = try root.relative(value)
    let parent = relative.utf16.split(separator: 47).dropLast().map { String(decoding: $0, as: UTF16.self) }.joined(separator: "/")
    return AIArtifactLocation(pcID: session.pcID, sessionID: session.id,
        directory: root.native(parent.isEmpty ? root.path : root.prefix + parent), filePath: root.native(root.prefix + relative))
}


struct AIWebConversation:UIViewRepresentable {
    @ObservedObject var workspace:AIWorkspace
    let session:AIConversation
    @Environment(\.colorScheme) private var colorScheme
    @AppStorage("ai-font-scale") private var fontScale=1.0
    func makeCoordinator()->Coordinator{Coordinator(workspace:workspace,session:session)}
    func makeUIView(context:Context)->WKWebView {
        let config=WKWebViewConfiguration();config.websiteDataStore = .nonPersistent()
        config.setURLSchemeHandler(BundledAIScheme(),forURLScheme:Coordinator.scheme)
        let scripts=WKUserContentController()
        scripts.add(context.coordinator,name:"TermLink")
        scripts.addUserScript(.init(source:"window.TermLink={postMessage:function(v){window.webkit.messageHandlers.TermLink.postMessage(v)}};",injectionTime:.atDocumentStart,forMainFrameOnly:true))
        config.userContentController=scripts
        let web=WKWebView(frame:.zero,configuration:config);web.navigationDelegate=context.coordinator;web.isOpaque=false;web.backgroundColor = .clear
        context.coordinator.web=web;web.load(URLRequest(url:Coordinator.index));return web
    }
    func updateUIView(_ web:WKWebView,context:Context){context.coordinator.update(session:session,dark:colorScheme == .dark,scale:fontScale)}
    static func dismantleUIView(_ web:WKWebView,coordinator:Coordinator){coordinator.retire();web.configuration.userContentController.removeScriptMessageHandler(forName:"TermLink");web.stopLoading();web.navigationDelegate=nil}

    @MainActor final class Coordinator:NSObject,WKScriptMessageHandler,WKNavigationDelegate {
        static let scheme="termlink-ai",index=URL(string:"termlink-ai://bundle/index.html")!
        weak var web:WKWebView?
        let workspace:AIWorkspace
        var current:AIConversation
        private var ready=false,dark=true,scale=1.0,retired=false,generation=UUID()
        private var tasks:[String:Task<Void,Never>]=[:],lastID="",lastStream=""
        init(workspace:AIWorkspace,session:AIConversation){self.workspace=workspace;self.current=session}
        func retire(){retired=true;generation=UUID();tasks.values.forEach{$0.cancel()};tasks=[:]}
        func update(session:AIConversation,dark:Bool,scale:Double){if current.id != session.id || current.pcID != session.pcID{generation=UUID();tasks.values.forEach{$0.cancel()};tasks=[:]};current=session;self.dark=dark;self.scale=scale;publish()}
        private func publish(){guard ready,!retired,let web else{return};var value=current.raw.object
            value["agent"] = .string(current.agent)
            for key in ["phase","diff","error"]{value[key] = .string(current.raw[key].string)}
            value["pending"] = .array(current.pending.map{pending in var p=pending.object;p["questions"] = .array(pending["questions"].array);for key in ["title","summary","detail"]{p[key] = .string(pending[key].string)};return .object(p)})
            if current.raw["usage"].string.isEmpty,let usage=try? current.raw["usage"].data(){value["usage"] = .string(current.raw["usage"] == .null ? "":String(decoding:usage,as:UTF8.self))}
            value["busy"] = .bool(workspace.busy.contains(current.pcID));value["uncertain"] = .bool(workspace.uncertain[current.pcID] != nil)
            value["dark"] = .bool(dark);value["fontScale"] = .number(min(2,max(0.85,scale)));value["language"] = .string(Locale.current.language.languageCode?.identifier ?? "zh")
            let c=current.capabilities;for k in ["artifacts","search","diffRead","agentFeatures","historyActions","pathStyle","hostPlatform"]{value[k]=c[k]}
            if let raw=try? JSONValue.decode(Data(value["plan"]?.string.utf8 ?? "".utf8)){value["plan"]=raw}else if value["plan"]?.array.isEmpty != false{value["plan"] = .array([])}
            let messages=current.messages.map{message->JSONValue in var result=message.object;for key in ["kind","text","title","summary","status"]{result[key] = .string(message[key].string)};result["fullLength"] = .number(Double(message["fullLength"].integer));return .object(result)}
            let payload=JSONValue.object(["session":.object(value),"messages":.array(messages),"order":.array(current.messages.map{$0["id"]}),"reset":.bool(lastID != current.id || lastStream != current.raw["streamId"].string),"uiState":workspace.webState(current)])
            guard let json=try? payload.data() else{return};lastID=current.id;lastStream=current.raw["streamId"].string
            web.evaluateJavaScript("window.termlinkUpdate && window.termlinkUpdate(\(String(decoding:json,as:UTF8.self)))",completionHandler:nil)
        }
        func userContentController(_ userContentController:WKUserContentController,didReceive message:WKScriptMessage){
            guard !retired,message.frameInfo.isMainFrame,message.frameInfo.securityOrigin.protocol == Self.scheme,message.frameInfo.securityOrigin.host == "bundle",web?.url == Self.index,
                  let text=message.body as? String,text.utf8.count<=65536,let request=try? JSONValue.decode(Data(text.utf8),maximum:65536),
                  !request["id"].string.isEmpty,request["id"].string.count<=80,tasks[request["id"].string]==nil else{return}
            let id=request["id"].string,owner=generation,session=current
            guard tasks.count<8 else{reply(id,error:"请等待当前操作完成");return}
            tasks[id]=Task{[weak self] in guard let self else{return};defer{self.tasks.removeValue(forKey:id)}
                do{let result=try await self.perform(request,session:session);guard !self.retired,self.generation==owner else{return};self.reply(id,value:result);self.publish()}
                catch is CancellationError{}catch{if self.generation==owner{self.reply(id,error:error.localizedDescription)}}
            }
        }
        private func reply(_ id:String,value:JSONValue = .null,error:String?=nil){var object:[String:JSONValue]=["id":.string(id)];if let error{object["error"] = .string(String(error.prefix(240)))}else{object["value"]=value};guard let data=try? JSONValue.object(object).data() else{return};web?.evaluateJavaScript("window.termlinkReply && window.termlinkReply(\(String(decoding:data,as:UTF8.self)))",completionHandler:nil)}
        private func relative(_ path:String,session:AIConversation) throws -> String {
            try aiArtifactRelativePath(directory: session.directory, value: path, pathStyle: session.capabilities["pathStyle"].string)
        }
        private func perform(_ request:JSONValue,session:AIConversation) async throws -> JSONValue {
            let action=request["action"].string,text=request["text"].string
            switch action {
            case "ready":ready=true;return .bool(true)
            case "back-state":return .bool(true)
            case "ui-state":try workspace.saveWebState(session,request["state"]);return .bool(true)
            case "copy":guard text.utf8.count<=1_048_577 else{throw ClientError.limitExceeded};UIPasteboard.general.string=text;return .bool(true)
            case "quote","edit":guard text.count<=65536 else{throw ClientError.limitExceeded};try workspace.appendDraft(session.id,action=="quote" ? "> "+text.replacingOccurrences(of:"\n",with:"\n> ")+"\n":text);return .bool(true)
            case "history":try await workspace.history(session);return .bool(true)
            case "message":return .string(try await workspace.fullMessage(session,id:request["messageId"].string))
            case "history.actions":guard session.capabilities["historyActions"].bool else{throw ClientError.readOnly};let r=try await workspace.read(session,action:"history.actions");return r["historyActions"]
            case "review","revert","fork":
                guard session.capabilities["historyActions"].bool,session.canControl,session.state=="idle",session.pending.isEmpty,request["confirmed"].bool,request["turnId"].string.count<=256 else{throw ClientError.readOnly}
                _=try await workspace.mutate(pc:session.pcID,action:action,session:session,extra:["requestId":request["turnId"]]);return .bool(true)
            case "agent.features":guard session.capabilities["agentFeatures"].bool else{throw ClientError.readOnly};let r=try await workspace.read(session,action:"agent.features");return r["agentFeatures"]
            case "search","artifacts":guard request["query"].string.count<=200,request["offset"].integer>=0,request["offset"].integer<=1_000_000,session.capabilities[action=="search" ? "search":"artifacts"].bool else{throw ClientError.readOnly};let r=try await workspace.read(session,action:action,extra:["text":request["query"],"offset":request["offset"]]);return r[action=="search" ? "search":"artifacts"]
            case "diff.read":guard session.capabilities["diffRead"].bool,request["offset"].integer>=0,request["offset"].integer<=262145 else{throw ClientError.readOnly};let r=try await workspace.read(session,action:action,extra:["offset":request["offset"],"text":request["version"]]);return r["diff"]
            case "artifact.read","open-file":guard session.capabilities["artifacts"].bool,request["offset"].integer>=0,request["offset"].integer<=50*1024*1024 else{throw ClientError.readOnly};let file=try relative(request[action=="open-file" ? "path":"fileId"].string,session:session);let r=try await workspace.read(session,action:"artifact.read",extra:["requestId":.string(file),"offset":request["offset"],"cursor":request["version"]]);return r["artifacts"]
            case "interrupt":_=try await workspace.mutate(pc:session.pcID,action:action,session:session);return .bool(true)
            case "answer":let id=request["requestId"].string;guard let pending=session.pending.first(where:{$0["id"].string==id}),["allow","deny"].contains(request["decision"].string),request["decision"].string=="deny" || pending["canApprove"].bool,try request["answers"].data().count<=24000 else{throw ClientError.readOnly};_=try await workspace.mutate(pc:session.pcID,action:action,session:session,extra:["requestId":.string(id),"decision":request["decision"],"answers":request["answers"]]);return .bool(true)
            case "open-link":guard let url=URL(string:request["url"].string),["https","http"].contains(url.scheme),url.host != nil,url.user==nil,url.password==nil,url.absoluteString.count<=4096 else{throw ClientError.invalidURL};workspace.externalLink=url;return .bool(true)
            case "open-directory":
                guard session.capabilities["artifacts"].bool else{throw ClientError.readOnly}
                let location=try aiArtifactLocation(session:session,value:request["fileId"].string)
                workspace.directoryLocation=(location.pcID,location.directory);return .bool(true)
            case "download":guard session.capabilities["artifacts"].bool else{throw ClientError.readOnly};let file=try relative(request["fileId"].string,session:session);try await workspace.downloadArtifact(session,file:file,version:request["version"].string);return .bool(true)
            default:throw ClientError.unavailable("不支持的 AI 页面操作。")
            }
        }
        func webView(_ webView:WKWebView,decidePolicyFor navigationAction:WKNavigationAction,decisionHandler:@escaping(WKNavigationActionPolicy)->Void){
            guard let url=navigationAction.request.url else{decisionHandler(.cancel);return}
            let bundled=url.scheme==Self.scheme && url.host=="bundle"
            let preview=navigationAction.targetFrame?.isMainFrame==false && ["about","blob"].contains(url.scheme)
            decisionHandler(bundled || preview ? .allow:.cancel)
        }
        func webViewWebContentProcessDidTerminate(_ webView:WKWebView){ready=false;lastID="";webView.load(URLRequest(url:Self.index))}
    }
}

final class BundledAIScheme:NSObject,WKURLSchemeHandler {
    func webView(_ webView:WKWebView,start urlSchemeTask:WKURLSchemeTask){
        do{guard let url=urlSchemeTask.request.url,url.host=="bundle",urlSchemeTask.request.httpMethod=="GET",let root=Bundle.main.url(forResource:"ai-web",withExtension:nil) else{throw ClientError.unavailable("AI Web 资源未随应用打包。")}
            let relative=url.path.removingPercentEncoding ?? "",parts=relative.split(separator:"/")
            guard !parts.contains(where:{$0==".." || $0=="."}),!relative.contains("\\"),!relative.contains("\0") else{throw ClientError.invalidURL}
            let file=root.appendingPathComponent(relative.trimmingCharacters(in:CharacterSet(charactersIn:"/"))).standardizedFileURL
            guard file.path.hasPrefix(root.standardizedFileURL.path+"/") else{throw ClientError.invalidURL}
            let data=try Data(contentsOf:file);guard data.count<=16*1024*1024 else{throw ClientError.limitExceeded}
            let mime=["html":"text/html","js":"text/javascript","css":"text/css","json":"application/json","svg":"image/svg+xml","png":"image/png","woff2":"font/woff2"][file.pathExtension] ?? "application/octet-stream"
            guard let response=HTTPURLResponse(url:url,statusCode:200,httpVersion:"HTTP/1.1",headerFields:["Content-Type":mime,"Content-Length":String(data.count),"Access-Control-Allow-Origin":"termlink-ai://bundle","X-Content-Type-Options":"nosniff","Cache-Control":"no-store"]) else{throw ClientError.invalidResponse("bundled resource")}
            urlSchemeTask.didReceive(response);urlSchemeTask.didReceive(data);urlSchemeTask.didFinish()
        }catch{urlSchemeTask.didFailWithError(error)}
    }
    func webView(_ webView:WKWebView,stop urlSchemeTask:WKURLSchemeTask){}
}
