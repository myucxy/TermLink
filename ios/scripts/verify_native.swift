#!/usr/bin/env swift
// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import Foundation

struct Lock: Decodable {
    struct Artifact: Decodable { let size: UInt64; let sha256: String; let path: String; let platform: String }
    let schema_version: Int; let revision: String; let source_dirty: Bool; let artifacts: [Artifact]
}

do {
    let script = URL(fileURLWithPath: #filePath).resolvingSymlinksInPath()
    let root = script.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    let lock = try JSONDecoder().decode(Lock.self, from: Data(contentsOf: root.appendingPathComponent("terminal-core.lock.json")))
    guard lock.schema_version == 2, !lock.source_dirty,
          lock.revision.count == 40, lock.revision.allSatisfy({ $0.isHexDigit }) else { throw NSError(domain: "TermLinkNativeLock", code: 1) }
    let directory = root.appendingPathComponent("native/terminal-core/ios/TermLinkTerminalCore.xcframework").resolvingSymlinksInPath()
    let artifacts = lock.artifacts.filter { $0.platform == "ios" }
    guard directory.path.hasPrefix(root.path+"/native/terminal-core/ios/"),!artifacts.isEmpty, FileManager.default.fileExists(atPath: directory.appendingPathComponent("Info.plist").path) else {
        throw NSError(domain: "iOS XCFramework and lock entries are missing", code: 2)
    }
    var recorded = Set<String>()
    for artifact in artifacts {
        let url = root.appendingPathComponent(artifact.path).resolvingSymlinksInPath()
        guard url.path.hasPrefix(directory.path + "/"), !artifact.path.contains(".."),
              recorded.insert(url.path).inserted, artifact.sha256.count == 64 else { throw NSError(domain: "Invalid iOS artifact path", code: 3) }
        let handle = try FileHandle(forReadingFrom: url); defer { try? handle.close() }
        var digest = SHA256(), size: UInt64 = 0
        while let block = try handle.read(upToCount: 65536), !block.isEmpty { size += UInt64(block.count); digest.update(data: block) }
        let actual = digest.finalize().map { String(format: "%02x", $0) }.joined()
        guard size == artifact.size, actual == artifact.sha256.lowercased() else { throw NSError(domain: "iOS artifact hash mismatch", code: 4) }
    }
    guard let files = FileManager.default.enumerator(at: directory, includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey]) else { throw NSError(domain: "Unreadable XCFramework", code: 5) }
    for case let file as URL in files {
        let values = try file.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
        guard values.isSymbolicLink != true else { throw NSError(domain: "Symbolic links are not accepted in the iOS artifact", code: 6) }
        if values.isRegularFile == true, !recorded.contains(file.resolvingSymlinksInPath().path) { throw NSError(domain: "Unrecorded iOS artifact file", code: 7) }
    }
    print("Verified iOS terminal-core revision \(lock.revision)")
} catch {
    fputs("error: iOS native artifact verification failed: \(error.localizedDescription)\n", stderr)
    exit(1)
}
