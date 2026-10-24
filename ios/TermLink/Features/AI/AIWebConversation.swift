// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI
import WebKit

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
        private var tasks:[String:Task<Void,Never>]=[:],lastID=""
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
            let c=current.capabilities;for k in ["artifacts","search","diffRead"]{value[k]=c[k]}
            if let raw=try? JSONValue.decode(Data(value["plan"]?.string.utf8 ?? "".utf8)){value["plan"]=raw}else if value["plan"]?.array.isEmpty != false{value["plan"] = .array([])}
            let messages=current.messages.map{message->JSONValue in var result=message.object;for key in ["kind","text","title","summary","status"]{result[key] = .string(message[key].string)};result["fullLength"] = .number(Double(message["fullLength"].integer));return .object(result)}
            let payload=JSONValue.object(["session":.object(value),"messages":.array(messages),"order":.array(current.messages.map{$0["id"]}),"reset":.bool(lastID != current.id),"uiState":workspace.webState(current)])
            guard let json=try? payload.data() else{return};lastID=current.id
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
            let normalized=path.replacingOccurrences(of:"\\",with:"/"),root=session.directory.replacingOccurrences(of:"\\",with:"/").trimmingCharacters(in:CharacterSet(charactersIn:"/"))
            let value=normalized.lowercased().hasPrefix(root.lowercased()+"/") ? String(normalized.dropFirst(root.count+1)):normalized
            guard value.utf8.count<=4096,!value.isEmpty,!value.hasPrefix("/"),!value.contains(":"),!value.contains("\0"),!value.split(separator:"/",omittingEmptySubsequences:false).contains(where:{$0==".." || $0=="." || $0.isEmpty}) else{throw ClientError.invalidResponse("artifact path")};return value
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
            case "search","artifacts":guard request["query"].string.count<=200,request["offset"].integer>=0,request["offset"].integer<=1_000_000,session.capabilities[action=="search" ? "search":"artifacts"].bool else{throw ClientError.readOnly};let r=try await workspace.read(session,action:action,extra:["text":request["query"],"offset":request["offset"]]);return r[action=="search" ? "search":"artifacts"]
            case "diff.read":guard session.capabilities["diffRead"].bool,request["offset"].integer>=0,request["offset"].integer<=262145 else{throw ClientError.readOnly};let r=try await workspace.read(session,action:action,extra:["offset":request["offset"],"text":request["version"]]);return r["diff"]
            case "artifact.read","open-file":guard session.capabilities["artifacts"].bool,request["offset"].integer>=0,request["offset"].integer<=50*1024*1024 else{throw ClientError.readOnly};let file=try relative(request[action=="open-file" ? "path":"fileId"].string,session:session);let r=try await workspace.read(session,action:"artifact.read",extra:["requestId":.string(file),"offset":request["offset"],"cursor":request["version"]]);return r["artifacts"]
            case "interrupt":_=try await workspace.mutate(pc:session.pcID,action:action,session:session);return .bool(true)
            case "answer":let id=request["requestId"].string;guard let pending=session.pending.first(where:{$0["id"].string==id}),["allow","deny"].contains(request["decision"].string),request["decision"].string=="deny" || pending["canApprove"].bool,try request["answers"].data().count<=24000 else{throw ClientError.readOnly};_=try await workspace.mutate(pc:session.pcID,action:action,session:session,extra:["requestId":.string(id),"decision":request["decision"],"answers":request["answers"]]);return .bool(true)
            case "open-link":guard let url=URL(string:request["url"].string),["https","http"].contains(url.scheme),url.host != nil,url.user==nil,url.password==nil,url.absoluteString.count<=4096 else{throw ClientError.invalidURL};workspace.externalLink=url;return .bool(true)
            case "open-directory":let file=try relative(request["fileId"].string,session:session);workspace.openDirectory(session,path:file);return .bool(true)
            case "download":let file=try relative(request["fileId"].string,session:session);try await workspace.downloadArtifact(session,file:file,version:request["version"].string);return .bool(true)
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
