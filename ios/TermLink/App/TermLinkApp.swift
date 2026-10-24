// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI
import UIKit

@main @MainActor struct TermLinkApp:App {
    @StateObject private var product=ProductStore()
    @StateObject private var shortcuts=TerminalShortcutStore()
    @Environment(\.scenePhase) private var phase
    @AppStorage("keep-screen-on") private var keepScreenOn=false
    var body:some Scene{WindowGroup{ProductRootView().environmentObject(product).environmentObject(shortcuts).task{await product.restore();UIApplication.shared.isIdleTimerDisabled=keepScreenOn}.onChange(of:phase){_,next in if next == .active{Task{await product.foreground()}}else if next == .background{product.background()}}}}
}
struct ProductRootView:View {
    @EnvironmentObject var product:ProductStore
    @Environment(\.horizontalSizeClass) private var size
    @AppStorage("appearance") private var appearance="system"
    var body:some View{
        Group{if size == .regular{
            NavigationSplitView{List(ProductTab.allCases,selection:Binding<ProductTab?>(get:{product.tab},set:{if let value=$0{product.tab=value}})){tab in Label(tab.title,systemImage:tab.symbol).tag(tab)}.navigationTitle("TermLink")}detail:{NavigationStack{destination(product.tab)}}
        }else{TabView(selection:$product.tab){ForEach(ProductTab.allCases){tab in NavigationStack{destination(tab)}.tabItem{Label(tab.title,systemImage:tab.symbol)}.tag(tab)}}}
        .tint(Color(red:0.61,green:0.70,blue:1)).preferredColorScheme(appearance=="dark" ? .dark:appearance=="light" ? .light:nil)
        .alert("操作未完成",isPresented:Binding(get:{product.error != nil},set:{if !$0{product.error=nil}})){Button("关闭",role:.cancel){product.error=nil}}message:{Text(product.error ?? "")}
    }
    @ViewBuilder private func destination(_ tab:ProductTab)->some View{switch tab{case .devices:DevicesView();case .cli:CLIPage();case .ai:AIPage(workspace:product.ai);case .settings:SettingsView()}}
}
