// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import UIKit

struct TerminalCell {
    let text: String, foreground: UInt32, background: UInt32, flags: UInt32, continuation: Bool
}
struct TerminalFrame {
    let columns: Int, rows: Int, cursorRow: Int, cursorColumn: Int, cursorVisible: Bool, nextSequence: UInt64
    let cells: [TerminalCell]
    let lineRenditions:[UInt8]
    static func decode(_ data: Data) throws -> Self {
        let b = Array(data)
        func n(_ offset: Int, _ size: Int) throws -> UInt64 {
            guard offset >= 0, size <= 8, offset <= b.count-size else { throw ClientError.protocolViolation("render bounds") }
            return (0..<size).reduce(0) { $0 | UInt64(b[offset+$1]) << ($1*8) }
        }
        guard b.count>=240,b.count<=32*1024*1024,Data(b.prefix(8))==Data("TLRFRM4\0".utf8),try n(8,2)==4,try n(10,2)==0,try n(12,2)==96,try n(14,2)==24,try n(16,2)==6,
              try n(20,4)==UInt64(b.count),try n(60,4)==1,try n(64,4)==96,try n(70,2)==0,try n(72,8)==0x7ff,try n(80,8)==0 else { throw ClientError.protocolViolation("render header") }
        func crc(_ bytes: ArraySlice<UInt8>) -> UInt32 {
            var crc: UInt32 = .max
            for byte in bytes { crc ^= UInt32(byte); for _ in 0..<8 { crc = (crc >> 1) ^ (crc & 1 == 1 ? 0x82f63b78:0) } }; return ~crc
        }
        guard crc(b[0..<92])==UInt32(try n(92,4)),crc(b[96...])==UInt32(try n(88,4)) else { throw ClientError.protocolViolation("render CRC32C") }
        let cols=Int(try n(40,4)),rows=Int(try n(44,4)),cursorRow=Int(try n(48,4)),cursorColumn=Int(try n(52,4))
        guard (1...512).contains(cols),(1...512).contains(rows),cursorRow<rows,cursorColumn<cols else { throw ClientError.protocolViolation("render dimensions") }
        var sections:[(offset:Int,size:Int,count:Int)]=[]
        var last=240
        for index in 0..<6 {
            let d=96+index*24,o=Int(try n(d+8,4)),len=Int(try n(d+12,4)),count=Int(try n(d+16,4))
            guard try n(d,4)==UInt64(index+1),try n(d+4,2)==(index==1 ? 2:1),try n(d+6,2)==1,o==last,len<=b.count-o else { throw ClientError.protocolViolation("render sections") }
            sections.append((o,len,count));last=(o+len+7) & ~7
        }
        guard last==b.count else { throw ClientError.protocolViolation("render trailing bytes") }
        let g=sections[0],r=sections[1],p=sections[2],grid=sections[4]
        guard g.count>0,g.count<=cols*rows,g.size>=g.count*8,r.count>0,r.count<=cols*rows,r.size==r.count*16,p.count==264,p.size==1056 else { throw ClientError.limitExceeded }
        var graphemes:[String]=[]
        var stringEnd=g.count*8
        for i in 0..<g.count {
            let off=Int(try n(g.offset+i*8,4)),len=Int(try n(g.offset+i*8+4,2))
            guard off==stringEnd,off<=g.size-len,(1...14).contains(len),(1...2).contains(try n(g.offset+i*8+6,1)),try n(g.offset+i*8+7,1)==0,let text=String(bytes:b[g.offset+off..<g.offset+off+len],encoding:.utf8) else { throw ClientError.protocolViolation("render grapheme") }
            graphemes.append(text);stringEnd=off+len
        }
        guard stringEnd==g.size else{throw ClientError.protocolViolation("grapheme trailing bytes")}
        func palette(_ index:Int,foreground:Bool)->UInt32{
            let raw=(try? n(p.offset+index*4,4)) ?? UInt64(UInt32.max)
            if raw<=0xffffff{return UInt32(raw)}
            if index>=256{return foreground ? 0xd0d0d0:0x080c10}
            let base:[UInt32]=[0x000000,0xcd0000,0x00cd00,0xcdcd00,0x0000ee,0xcd00cd,0x00cdcd,0xe5e5e5,0x7f7f7f,0xff0000,0x00ff00,0xffff00,0x5c5cff,0xff00ff,0x00ffff,0xffffff]
            if index<16{return base[index]};if index>=232{let level=UInt32(8+(index-232)*10);return level<<16|level<<8|level}
            let rgb=index-16,levels:[UInt32]=[0,95,135,175,215,255];return levels[rgb/36]<<16|levels[(rgb/6)%6]<<8|levels[rgb%6]
        }
        func color(_ value: UInt32, defaultIndex: Int) throws -> UInt32 {
            switch value >> 24 {
            case 0: guard value==0 else{throw ClientError.protocolViolation("default color")};return palette(defaultIndex,foreground:defaultIndex==256)
            case 1: guard value&0xffffff<256 else { throw ClientError.protocolViolation("palette") };return palette(Int(value&255),foreground:defaultIndex==256)
            case 2: return value&0xffffff
            default: throw ClientError.protocolViolation("color")
            }
        }
        var renditions:[(UInt32,UInt32,UInt32)]=[]
        for i in 0..<r.count { let off=r.offset+i*16; renditions.append((try color(UInt32(n(off+4,4)),defaultIndex:256),try color(UInt32(n(off+8,4)),defaultIndex:257),UInt32(try n(off,4)))) }
        // Rows/cells are decoded from the core's canonical V4 renderer format, never from VT text.
        let prefix=8+rows*8
        guard grid.size==prefix+cols*rows*12,try n(grid.offset,4)==8,try n(grid.offset+4,4)==12 else { throw ClientError.protocolViolation("render grid") }
        var cells:[TerminalCell]=[],lineRenditions:[UInt8]=[];cells.reserveCapacity(cols*rows)
        for row in 0..<rows{let flags=try n(grid.offset+8+row*8+4,4);guard flags & ~UInt64(7)==0,try n(grid.offset+8+row*8,4)<=UInt64(flags>>1==0 ? cols:max(1,cols/2)) else{throw ClientError.protocolViolation("row metadata")};lineRenditions.append(UInt8(flags>>1))}
        for i in 0..<cols*rows {
            let off=grid.offset+prefix+i*12,gi=Int(try n(off,4)),ri=Int(try n(off+4,4)),flags=UInt32(try n(off+8,4))
            guard gi<=graphemes.count,ri<renditions.count,(flags&1 != 0)==(gi==0) else { throw ClientError.protocolViolation("cell indexes") }
            let rendition=renditions[ri],inverse=(rendition.2&8 != 0) != ((try n(18,2))&2 != 0)
            cells.append(.init(text:gi==0 ? "":graphemes[gi-1],foreground:inverse ? rendition.1:rendition.0,background:inverse ? rendition.0:rendition.1,flags:rendition.2,continuation:flags&1 != 0))
        }
        return Self(columns:cols,rows:rows,cursorRow:cursorRow,cursorColumn:cursorColumn,cursorVisible:try n(56,4)&1 != 0,nextSequence:try n(32,8),cells:cells,lineRenditions:lineRenditions)
    }
}
extension UIColor {
    convenience init(rgb: UInt32) { self.init(red:CGFloat((rgb>>16)&255)/255,green:CGFloat((rgb>>8)&255)/255,blue:CGFloat(rgb&255)/255,alpha:1) }
}
