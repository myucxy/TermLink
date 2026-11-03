// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// Untrusted OSC 7/133 annotations from terminal-core. Decoding never opens paths or runs commands.
struct TerminalShellPoint: Equatable {
    let sourceSequence: UInt64
    let lineID: UInt64
    let cellOffset: UInt32
    let page: UInt8
    let alternate: Bool
}

struct TerminalShellCommand: Equatable, Identifiable {
    enum Phase: UInt8 {
        case prompt = 0, input = 1, output = 2, completed = 3, interrupted = 4
    }
    let id: UInt64
    let phase: Phase
    let prompt: TerminalShellPoint
    let input: TerminalShellPoint?
    let output: TerminalShellPoint?
    let end: TerminalShellPoint?
    let exitCode: Int32?
    let currentDirectoryURI: String
}

struct TerminalShellState: Equatable {
    static let maxBytes = 273_696
    static let empty = Self(currentDirectoryURI: "", commands: [], nextCommandID: 1)

    let currentDirectoryURI: String
    let commands: [TerminalShellCommand]
    let nextCommandID: UInt64

    /// Row IDs may be reused after a hard reset. A cached UI entry must still
    /// belong to the current command list with the same full prompt identity.
    func command(matching cached: TerminalShellCommand) -> TerminalShellCommand? {
        commands.first { $0.id == cached.id && $0.prompt == cached.prompt }
    }

    /// TLSHV1 is defined by terminal-core's shell_integration.rs. Its version is in the
    /// magic; there is no revision or CRC field. Anchor bounds come from the same core
    /// checkpoint's next source sequence, never from shell-provided metadata.
    static func decode(_ data: Data, nextSequence: UInt64) throws -> Self {
        guard (32...maxBytes).contains(data.count) else { throw malformed() }
        let bytes = Array(data)
        func number(_ offset: Int, _ count: Int) throws -> UInt64 {
            guard offset >= 0, (1...8).contains(count), offset <= bytes.count - count else { throw malformed() }
            return (0..<count).reduce(0) { $0 | UInt64(bytes[offset + $1]) << ($1 * 8) }
        }
        guard Array(bytes.prefix(8)) == Array("TLSHV1\0\0".utf8), try number(24, 8) == 0 else { throw malformed() }
        let nextID = try number(8, 8), count = Int(try number(20, 4))
        guard nextID > 0, count <= 64 else { throw malformed() }
        var offset = 32
        func directory(_ length: Int) throws -> String {
            guard length <= 4096, offset <= bytes.count - length,
                  let value = String(bytes: bytes[offset..<offset + length], encoding: .utf8) else { throw malformed() }
            offset += length
            if !value.isEmpty { _ = try directoryURI(value) }
            return value
        }
        let current = try directory(Int(number(16, 4)))
        var commands: [TerminalShellCommand] = []
        commands.reserveCapacity(count)
        var previousID: UInt64 = 0, previousSequence: UInt64 = 0
        for _ in 0..<count {
            guard offset <= bytes.count - 116 else { throw malformed() }
            let record = offset
            offset += 116
            let id = try number(record, 8), flags = bytes[record + 9]
            let code = Int32(bitPattern: UInt32(try number(record + 12, 4)))
            guard let phase = TerminalShellCommand.Phase(rawValue: bytes[record + 8]),
                  id > previousID, id < nextID, flags & ~UInt8(15) == 0,
                  try number(record + 10, 2) == 0,
                  flags & 8 != 0 || code == 0,
                  flags & 8 == 0 || flags & 4 != 0,
                  (phase == .completed) == (flags & 4 != 0),
                  phase != .prompt || flags == 0,
                  phase != .input || flags == 1,
                  phase != .output || flags & 2 != 0 else { throw malformed() }
            var points: [TerminalShellPoint?] = []
            for index in 0..<4 {
                let position = record + 16 + index * 24
                if index != 0, flags & (1 << (index - 1)) == 0 {
                    guard bytes[position..<position + 24].allSatisfy({ $0 == 0 }) else { throw malformed() }
                    points.append(nil)
                    continue
                }
                let sequence = try number(position, 8), line = try number(position + 8, 8)
                let cell = UInt32(try number(position + 16, 4)), page = bytes[position + 20], alternate = bytes[position + 21]
                guard sequence >= previousSequence, sequence < nextSequence, (1...6).contains(page),
                      alternate <= 1, try number(position + 22, 2) == 0,
                      alternate == 0 || page == 1 else { throw malformed() }
                previousSequence = sequence
                points.append(.init(sourceSequence: sequence, lineID: line, cellOffset: cell, page: page, alternate: alternate == 1))
            }
            let cwd = try directory(Int(number(record + 112, 4)))
            guard let prompt = points[0] else { throw malformed() }
            commands.append(.init(id: id, phase: phase, prompt: prompt, input: points[1], output: points[2],
                                  end: points[3], exitCode: flags & 8 == 0 ? nil : code, currentDirectoryURI: cwd))
            previousID = id
        }
        guard offset == bytes.count else { throw malformed() }
        return Self(currentDirectoryURI: current, commands: commands, nextCommandID: nextID)
    }

    /// Matches Android's localWindowsDirectory: remote authorities, UNC/device paths,
    /// alternate data streams and traversal never become actionable directory paths.
    static func localWindowsDirectory(_ uri: String) -> String? {
        guard let (host, path) = try? directoryURI(uri), host.isEmpty || host.lowercased() == "localhost" else { return nil }
        let scalars = Array(path.unicodeScalars)
        guard scalars.count >= 4, scalars[0] == "/", scalars[2] == ":", scalars[3] == "/",
              (65...90).contains(scalars[1].value) || (97...122).contains(scalars[1].value) else { return nil }
        let remainder = String(String.UnicodeScalarView(scalars.dropFirst(3)))
        guard !remainder.contains(where: { "\\:<>\"|?*".contains($0) }) else { return nil }
        let parts = remainder.dropFirst().split(separator: "/", omittingEmptySubsequences: false)
        guard !parts.contains(where: { $0 == "." || $0 == ".." || $0.hasSuffix(".") || $0.hasSuffix(" ") }) else { return nil }
        return String(path.dropFirst()).replacingOccurrences(of: "/", with: "\\")
    }

    private static func directoryURI(_ uri: String) throws -> (host: String, path: String) {
        guard uri.utf8.count <= 4096, uri.hasPrefix("file://"),
              !uri.unicodeScalars.contains(where: isControl), !uri.contains(where: { "\\?#".contains($0) }) else { throw malformed() }
        let tail = uri.dropFirst(7)
        guard let slash = tail.firstIndex(of: "/") else { throw malformed() }
        let host = String(tail[..<slash])
        guard host.utf8.allSatisfy({ (65...90).contains($0) || (97...122).contains($0) || (48...57).contains($0) || [46, 45, 95].contains($0) }) else { throw malformed() }
        let raw = Array(tail[slash...].utf8)
        var decoded: [UInt8] = []
        decoded.reserveCapacity(raw.count)
        var index = 0
        while index < raw.count {
            if raw[index] == 37 {
                guard index + 2 < raw.count, let high = hex(raw[index + 1]), let low = hex(raw[index + 2]) else { throw malformed() }
                decoded.append(high * 16 + low)
                index += 3
            } else {
                decoded.append(raw[index])
                index += 1
            }
        }
        guard let path = String(bytes: decoded, encoding: .utf8), !path.unicodeScalars.contains(where: isControl) else { throw malformed() }
        return (host, path)
    }

    private static func hex(_ byte: UInt8) -> UInt8? {
        switch byte {
        case 48...57: return byte - 48
        case 65...70: return byte - 65 + 10
        case 97...102: return byte - 97 + 10
        default: return nil
        }
    }
    private static func isControl(_ scalar: Unicode.Scalar) -> Bool { scalar.value <= 0x1f || (0x7f...0x9f).contains(scalar.value) }
    private static func malformed() -> ClientError { .protocolViolation("shell state") }
}
