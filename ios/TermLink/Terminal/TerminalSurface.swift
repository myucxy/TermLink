// SPDX-License-Identifier: GPL-3.0-only
import SwiftUI
import UIKit

struct TerminalSurface: UIViewRepresentable {
    @ObservedObject var controller:TerminalController
    var fontSize:CGFloat
    func makeUIView(context:Context)->NativeTerminalView{let v=NativeTerminalView();v.sessionID=controller.session.id;v.onResize={columns,rows,w,h in controller.fit(columns:columns,rows:rows,cellWidth:w,cellHeight:h)};v.sendText={text in Task{do{try await controller.text(text)}catch{controller.present(error)}}};v.sendKey={key,mods in Task{do{try await controller.key(key,modifiers:mods)}catch{controller.present(error)}}};return v}
    func updateUIView(_ view:NativeTerminalView,context:Context){view.frameValue=controller.frame;view.inputAllowed=controller.canInput;view.fontSize=fontSize;view.setNeedsDisplay()}
}

final class NativeTerminalView:UIView,UITextViewDelegate {
    var frameValue:TerminalFrame?
    var inputAllowed=false{didSet{input.isEditable=inputAllowed;if !inputAllowed{input.resignFirstResponder()}}}
    var fontSize:CGFloat=14{didSet{if oldValue != fontSize{lastLayout = .zero;setNeedsLayout()}}}
    var sessionID: UUID?
    var sendText:((String)->Void)?,sendKey:((UInt32,UInt32)->Void)?
    var onResize:((Int,Int,Int,Int)->Void)?
    private var lastLayout:CGSize = .zero
    private let input=TerminalInputTextView()
    private var selectionStart:CGPoint?,selectionEnd:CGPoint?
    override init(frame:CGRect){super.init(frame:frame);backgroundColor=UIColor(rgb:0x080c10);isAccessibilityElement=true;accessibilityLabel="远程终端";clipsToBounds=true
        input.delegate=self;input.text="\u{200B}";input.backgroundColor = .clear;input.textColor = .clear;input.tintColor = .clear;input.autocorrectionType = .no;input.spellCheckingType = .no;input.smartDashesType = .no;input.smartQuotesType = .no;input.autocapitalizationType = .none;input.returnKeyType = .default;input.isScrollEnabled=false
        input.onDelete={ [weak self] in self?.sendKey?(3,0) };input.onInterrupt={ [weak self] in self?.sendText?("\u{3}") };input.onKey={ [weak self] key,mods in self?.sendKey?(key,mods) };addSubview(input)
        addGestureRecognizer(UITapGestureRecognizer(target:self,action:#selector(focus)))
        let long=UILongPressGestureRecognizer(target:self,action:#selector(selectText(_:)));addGestureRecognizer(long)
        addInteraction(UIContextMenuInteraction(delegate:self))
        NotificationCenter.default.addObserver(self,selector:#selector(showKeyboard(_:)),name:.terminalShowKeyboard,object:nil)
    }
    required init?(coder:NSCoder){fatalError("init(coder:) has not been implemented")}
    deinit { NotificationCenter.default.removeObserver(self) }
    override func layoutSubviews(){super.layoutSubviews();let point=cursorPoint;input.frame=CGRect(x:point.x,y:point.y,width:max(30,bounds.width-point.x),height:32)
        if bounds.size != lastLayout{lastLayout=bounds.size;if bounds.width>0,bounds.height>0{onResize?(Int(bounds.width/cell.width),Int(bounds.height/cell.height),Int(ceil(cell.width*contentScaleFactor)),Int(ceil(cell.height*contentScaleFactor)))}}
    }
    private var font:UIFont{UIFont.monospacedSystemFont(ofSize:fontSize,weight:.regular)}
    private var cell:CGSize{let w=("M" as NSString).size(withAttributes:[.font:font]).width;return .init(width:w,height:ceil(font.lineHeight))}
    private var cursorPoint:CGPoint{guard let f=frameValue else{return .zero};return .init(x:CGFloat(f.cursorColumn)*cell.width*(f.lineRenditions[f.cursorRow]==0 ? 1:2),y:CGFloat(f.cursorRow)*cell.height)}
    override func draw(_ rect:CGRect){guard let f=frameValue,let context=UIGraphicsGetCurrentContext() else{return};let size=cell
        for row in 0..<f.rows {
            let rendition=f.lineRenditions[row],scaled=rendition != 0
            context.saveGState();context.clip(to:CGRect(x:0,y:CGFloat(row)*size.height,width:bounds.width,height:size.height))
            context.translateBy(x:0,y:CGFloat(row)*size.height-(rendition==3 ? size.height:0));context.scaleBy(x:scaled ? 2:1,y:rendition>=2 ? 2:1)
            for column in 0..<(scaled ? max(1,f.columns/2):f.columns) {
            let c=f.cells[row*f.columns+column],r=CGRect(x:CGFloat(column)*size.width,y:0,width:size.width,height:size.height)
            context.setFillColor(UIColor(rgb:c.background).cgColor);context.fill(r)
            if !c.continuation && c.flags&32==0{var attributes:[NSAttributedString.Key:Any]=[.font:font,.foregroundColor:UIColor(rgb:c.foreground)];if c.flags&1 != 0{attributes[.font]=UIFont.monospacedSystemFont(ofSize:fontSize,weight:.bold)};if c.flags&16 != 0{attributes[.underlineStyle]=NSUnderlineStyle.single.rawValue};(c.text as NSString).draw(at:r.origin,withAttributes:attributes)}
        };context.restoreGState()}
        if f.cursorVisible{context.setFillColor(UIColor.white.withAlphaComponent(0.45).cgColor);context.fill(CGRect(origin:cursorPoint,size:size))}
        accessibilityValue=f.cells.enumerated().map{$0.offset%f.columns==f.columns-1 ? $0.element.text+"\n":$0.element.text}.joined()
        setNeedsLayout()
    }
    @objc private func focus(){if inputAllowed{input.becomeFirstResponder()}}
    @objc private func showKeyboard(_ notification:Notification){if notification.object as? UUID == sessionID { focus() }}
    func textViewDidChange(_ textView:UITextView){guard inputAllowed,textView.markedTextRange==nil else{return};let text=textView.text.replacingOccurrences(of:"\u{200B}",with:"");if !text.isEmpty{sendText?(text)};textView.text="\u{200B}"}
    @objc private func selectText(_ gesture:UILongPressGestureRecognizer){if gesture.state == .began{selectionStart=gesture.location(in:self)};selectionEnd=gesture.location(in:self);if gesture.state == .ended{UIAccessibility.post(notification:.announcement,argument:"长按菜单可复制所选终端文本。")}}
    private var selectedText:String{guard let f=frameValue else{return ""};guard let a=selectionStart,let b=selectionEnd else{return f.cells.enumerated().map{$0.offset%f.columns==f.columns-1 ? $0.element.text+"\n":$0.element.text}.joined()};let first=max(0,min(f.rows-1,Int(min(a.y,b.y)/cell.height))),last=max(0,min(f.rows-1,Int(max(a.y,b.y)/cell.height)));return (first...last).map{row in f.cells[(row*f.columns)..<((row+1)*f.columns)].filter{!$0.continuation}.map(\.text).joined()}.joined(separator:"\n")}
    fileprivate func copySelection(){UIPasteboard.general.string=selectedText}
}
extension NativeTerminalView:UIContextMenuInteractionDelegate{
    func contextMenuInteraction(_ interaction:UIContextMenuInteraction,configurationForMenuAtLocation location:CGPoint)->UIContextMenuConfiguration?{
        .init(identifier:nil,previewProvider:nil){[weak self] _ in UIMenu(children:[UIAction(title:"复制",image:UIImage(systemName:"doc.on.doc")){_ in self?.copySelection()}])}
    }
}
final class TerminalInputTextView:UITextView {
    var onDelete:(()->Void)?,onInterrupt:(()->Void)?,onKey:((UInt32,UInt32)->Void)?
    override func deleteBackward(){if markedTextRange != nil{super.deleteBackward()}else{onDelete?()}}
    override var keyCommands:[UIKeyCommand]?{[
        UIKeyCommand(input:UIKeyCommand.inputUpArrow,modifierFlags:[],action:#selector(up)),UIKeyCommand(input:UIKeyCommand.inputDownArrow,modifierFlags:[],action:#selector(down)),
        UIKeyCommand(input:UIKeyCommand.inputLeftArrow,modifierFlags:[],action:#selector(left)),UIKeyCommand(input:UIKeyCommand.inputRightArrow,modifierFlags:[],action:#selector(right)),
        UIKeyCommand(input:UIKeyCommand.inputEscape,modifierFlags:[],action:#selector(escape)),UIKeyCommand(input:"c",modifierFlags:.control,action:#selector(interrupt))]}
    @objc private func up(){onKey?(12,0)};@objc private func down(){onKey?(13,0)};@objc private func left(){onKey?(14,0)};@objc private func right(){onKey?(15,0)};@objc private func escape(){onKey?(5,0)};@objc private func interrupt(){onInterrupt?()}
}
