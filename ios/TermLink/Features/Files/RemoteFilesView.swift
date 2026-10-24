// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI
import UniformTypeIdentifiers

struct RemoteFilesView:View {
    @ObservedObject var store:RemoteFileStore
    let pc:PC
    var initialPath=""
    @EnvironmentObject var product:ProductStore
    @Environment(\.dismiss) private var dismiss
    var body:some View{VStack(spacing:0){HStack{Button("上一级",systemImage:"arrow.up"){Task{await store.list(pc:pc.id,path:store.parentPath ?? "")}}.disabled(store.parentPath==nil);Text(store.path.isEmpty ? "电脑磁盘":store.path).font(.caption).lineLimit(2);Spacer();Button("收藏",systemImage:store.currentFavorites.contains(store.path) ? "star.fill":"star"){store.toggleFavorite()}}.padding(10)
        HStack{TextField("筛选名称",text:$store.query).textFieldStyle(.roundedBorder);Button("搜索"){Task{await store.list(pc:pc.id,path:store.path)}}}.padding(.horizontal)
        if store.busy{ProgressView().padding()};if let error=store.error{Text(error).foregroundStyle(.red).font(.caption).padding()}
        List{if !store.currentFavorites.isEmpty{Section("收藏目录"){ForEach(store.currentFavorites,id:\.self){path in Button(path){Task{await store.list(pc:pc.id,path:path)}}}}};Section{ForEach(store.entries){entry in HStack{Button{if entry.directory{Task{await store.list(pc:pc.id,path:entry.id)}}}label:{Label(entry.name,systemImage:entry.directory ? "folder":"doc").frame(maxWidth:.infinity,alignment:.leading)};Menu{Button(entry.directory ? "下载目录 ZIP":"下载文件"){Task{do{try await store.download(entry,pc:pc)}catch{product.report(error)}}};if entry.directory{ForEach(pc.commands,id:\.self){command in Button("在此打开 \(command["display_name"].string)"){Task{do{try await store.openShell(pc:pc.id,path:entry.id,command:command["id"].string);dismiss()}catch{product.report(error)}}}}}}label:{Image(systemName:"ellipsis").frame(width:40,height:40)}}};if let offset=store.nextOffset{Button("加载更多"){Task{await store.list(pc:pc.id,path:store.path,offset:offset)}}}}}
    }.navigationTitle(pc.name).navigationBarTitleDisplayMode(.inline).toolbar{Button("关闭"){dismiss()}}.task{await store.list(pc:pc.id,path:initialPath)}}
}
struct DownloadsView:View {
    @ObservedObject var store:RemoteFileStore
    @Environment(\.dismiss) private var dismiss
    var body:some View{List{ForEach(store.downloads){record in VStack(alignment:.leading,spacing:7){Text(record.filename).font(.headline);Text(record.status).font(.caption);if record.expectedBytes>0{ProgressView(value:Double(record.received),total:Double(record.expectedBytes))};if let error=record.error{Text(error).font(.caption).foregroundStyle(.orange)};HStack{if record.status=="COMPLETED",let url=try? store.localFile(record){ShareLink(item:url){Label("导出到文件",systemImage:"square.and.arrow.up")}}else{Button("重试"){store.retry(record.id)};Button("取消"){store.cancel(record.id)}};Button("移除记录",role:.destructive){store.removeRecord(record.id)}}}}.navigationTitle("下载").toolbar{Button("关闭"){dismiss()}}}
}
