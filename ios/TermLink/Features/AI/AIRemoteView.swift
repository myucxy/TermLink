// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI
import WebKit

enum ZCodeRemoteURL {
    static func allowed(_ url: URL) -> Bool {
        let value = url.absoluteString
        guard !url.path.split(separator:"/",omittingEmptySubsequences:false).contains(where:{$0=="." || $0==".." || $0.contains("\\") || $0.contains("\0")}) else{return false}
        return value.count <= 8192 && !value.contains(where: { $0.isWhitespace || $0.asciiValue.map { $0 < 32 || $0 == 127 } == true || $0 == "\\" }) &&
            url.scheme?.lowercased() == "https" && url.host?.lowercased() == "zcode.z.ai" &&
            (url.port == nil || url.port == 443) && url.user == nil && url.password == nil &&
            (url.standardized.path == "/remote" || url.standardized.path.hasPrefix("/remote/"))
    }
}

struct AIRemoteView: View {
    @ObservedObject var workspace: AIWorkspace
    let pc: UUID
    @EnvironmentObject private var product: ProductStore
    @State private var address: URL?, error: String?, reload = UUID()
    var body: some View {
        VStack(spacing: 0) {
            if workspace.unavailable.contains(pc) { ContentUnavailableView("电脑连接待确认", systemImage: "cloud.slash") }
            else if let address { RemoteAgentWeb(url: address) }
            else if let error { ContentUnavailableView("无法打开 ZCode", systemImage: "exclamationmark.triangle", description: Text(error)) }
            else { ProgressView("获取远控地址…") }
        }.navigationTitle("ZCode").navigationBarTitleDisplayMode(.inline)
            .toolbar {
                Button("重新获取", systemImage: "arrow.clockwise") { reload = UUID() }
                if let address { Link(destination: address) { Image(systemName: "safari") } }
            }
            .task(id: reload) { address = nil; error = nil; do { address = try await workspace.remoteAddress(pc) } catch { self.error = error.localizedDescription } }
            .onDisappear { address = nil }
    }
}

struct RemoteAgentWeb: UIViewRepresentable {
    let url: URL
    func makeCoordinator() -> Coordinator { Coordinator() }
    func makeUIView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration(); config.websiteDataStore = .nonPersistent()
        let web = WKWebView(frame: .zero, configuration: config)
        web.navigationDelegate = context.coordinator; web.uiDelegate = context.coordinator
        if ZCodeRemoteURL.allowed(url) { web.load(URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData)) }
        return web
    }
    func updateUIView(_ view: WKWebView, context: Context) {}
    static func dismantleUIView(_ view: WKWebView, coordinator: Coordinator) { view.stopLoading(); view.navigationDelegate = nil; view.uiDelegate = nil }
    final class Coordinator: NSObject, WKNavigationDelegate, WKUIDelegate {
        func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            guard let url = action.request.url else { decisionHandler(.cancel); return }
            decisionHandler(action.targetFrame?.isMainFrame == false ? (url.scheme == "https" ? .allow : .cancel) : (ZCodeRemoteURL.allowed(url) ? .allow : .cancel))
        }
        func webView(_ webView: WKWebView, requestMediaCapturePermissionFor origin: WKSecurityOrigin, initiatedByFrame frame: WKFrameInfo, type: WKMediaCaptureType, decisionHandler: @escaping (WKPermissionDecision) -> Void) { decisionHandler(.deny) }
        // There is deliberately no TermLink native bridge or application token in this remote website.
    }
}
