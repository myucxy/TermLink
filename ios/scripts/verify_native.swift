#!/usr/bin/env swift
// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import Foundation

private let frameworkRelative = "native/terminal-core/ios/TermLinkTerminalCore.xcframework"
private let frameworkName = "TermLinkTerminalCore"
private let manager = FileManager.default

private struct VerificationError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

private struct NativeLock: Decodable {
    struct Artifact: Decodable {
        let size: UInt64
        let sha256: String
        let path: String
        let platform: String
    }
    let schema_version: Int
    let repository: String
    let revision: String
    let source_dirty: Bool
    let toolchain: String
    let artifacts: [Artifact]
}

private struct XCFrameworkInfo: Decodable {
    struct Library: Decodable {
        let LibraryIdentifier: String
        let LibraryPath: String
        let SupportedArchitectures: [String]
        let SupportedPlatform: String
        let SupportedPlatformVariant: String?
    }
    let CFBundlePackageType: String
    let XCFrameworkFormatVersion: String
    let AvailableLibraries: [Library]
}

private struct FrameworkInfo: Decodable {
    let CFBundleExecutable: String
    let CFBundlePackageType: String
    let CFBundleSupportedPlatforms: [String]
    let DTPlatformName: String
    let MinimumOSVersion: String
}

private func matches(_ value: String, _ pattern: String) -> Bool {
    value.range(of: pattern, options: .regularExpression) == value.startIndex..<value.endIndex
}

private func components(_ relative: String) throws -> [String] {
    let parts = relative.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
    guard !relative.isEmpty, parts.allSatisfy({
        $0 != "." && $0 != ".." && matches($0, "^[A-Za-z0-9_][A-Za-z0-9_.-]*$")
    }) else { throw VerificationError(message: "Invalid artifact path: \(relative)") }
    return parts
}

/// Inspect each component before reading it; resolving a symlink first would hide it.
private func checkedURL(root: URL, relative: String, file: Bool) throws -> URL {
    let parts = try components(relative)
    var current = root
    for (index, part) in parts.enumerated() {
        current.appendPathComponent(part)
        let type = try manager.attributesOfItem(atPath: current.path)[.type] as? FileAttributeType
        let expected: FileAttributeType = index == parts.count - 1 && file ? .typeRegular : .typeDirectory
        guard type == expected else {
            throw VerificationError(message: "Artifact path contains a symlink or non-regular entry: \(relative)")
        }
    }
    return current
}

private func smallData(_ url: URL) throws -> Data {
    let size = try manager.attributesOfItem(atPath: url.path)[.size] as? NSNumber
    guard let size, size.uint64Value <= 1_048_576 else {
        throw VerificationError(message: "Native metadata exceeds its size limit")
    }
    return try Data(contentsOf: url)
}

private func verifyDigest(_ artifact: NativeLock.Artifact, at url: URL) throws {
    let handle = try FileHandle(forReadingFrom: url)
    defer { try? handle.close() }
    var digest = SHA256(), size: UInt64 = 0
    while let block = try handle.read(upToCount: 65_536), !block.isEmpty {
        guard UInt64(block.count) <= artifact.size - min(size, artifact.size) else {
            throw VerificationError(message: "iOS artifact size mismatch: \(artifact.path)")
        }
        size += UInt64(block.count)
        digest.update(data: block)
    }
    let actual = digest.finalize().map { String(format: "%02x", $0) }.joined()
    guard size == artifact.size, actual == artifact.sha256 else {
        throw VerificationError(message: "iOS artifact hash/size mismatch: \(artifact.path)")
    }
}

private func inventory(root: URL, relative: String) throws -> Set<String> {
    let directory = try checkedURL(root: root, relative: relative, file: false)
    let children = try manager.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)
    var files = Set<String>(), names = Set<String>()
    for child in children {
        let name = child.lastPathComponent
        _ = try components(name)
        guard names.insert(name.lowercased()).inserted else {
            throw VerificationError(message: "Case-colliding artifact names")
        }
        let path = relative + "/" + name
        let type = try manager.attributesOfItem(atPath: child.path)[.type] as? FileAttributeType
        if type == .typeDirectory {
            files.formUnion(try inventory(root: root, relative: path))
        } else if type == .typeRegular {
            files.insert(path)
        } else {
            throw VerificationError(message: "Artifact symlinks and special files are forbidden: \(path)")
        }
    }
    return files
}

private func verifyFrameworkLayout(root: URL) throws {
    let infoURL = try checkedURL(root: root, relative: frameworkRelative + "/Info.plist", file: true)
    let info = try PropertyListDecoder().decode(XCFrameworkInfo.self, from: smallData(infoURL))
    guard info.CFBundlePackageType == "XFWK", info.XCFrameworkFormatVersion == "1.0",
          info.AvailableLibraries.count == 2 else {
        throw VerificationError(message: "XCFramework requires exactly two iOS platform slices")
    }
    var variants = Set<String>(), identifiers = Set<String>()
    for library in info.AvailableLibraries {
        let identifierParts = try components(library.LibraryIdentifier)
        let variant = library.SupportedPlatformVariant ?? "device"
        let simulator = variant == "simulator"
        let architectures = Set(library.SupportedArchitectures)
        guard identifierParts.count == 1, identifiers.insert(library.LibraryIdentifier.lowercased()).inserted,
              library.LibraryPath == frameworkName + ".framework", library.SupportedPlatform == "ios",
              library.SupportedPlatformVariant == nil || library.SupportedPlatformVariant == "simulator",
              variants.insert(variant).inserted,
              architectures.count == library.SupportedArchitectures.count,
              architectures == (simulator ? Set(["arm64", "x86_64"]) : Set(["arm64"])) else {
            throw VerificationError(message: "Invalid XCFramework iOS slice platform or architectures")
        }
        let base = frameworkRelative + "/" + library.LibraryIdentifier + "/" + library.LibraryPath
        let binary = try checkedURL(root: root, relative: base + "/" + frameworkName, file: true)
        let binarySize = try manager.attributesOfItem(atPath: binary.path)[.size] as? NSNumber
        guard let binarySize, binarySize.uint64Value > 0 else {
            throw VerificationError(message: "Framework static library is empty")
        }
        _ = try checkedURL(root: root, relative: base + "/Headers/termlink_terminal.h", file: true)
        let module = try checkedURL(root: root, relative: base + "/Modules/module.modulemap", file: true)
        let expectedModule = "framework module TermLinkTerminalCore { umbrella header \"termlink_terminal.h\" export * module * { export * } }"
        let actualModule = String(decoding: try smallData(module), as: UTF8.self).split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
        guard actualModule == expectedModule else {
            throw VerificationError(message: "Framework module/header contract is invalid")
        }
        let frameworkInfoURL = try checkedURL(root: root, relative: base + "/Info.plist", file: true)
        let frameworkInfo = try PropertyListDecoder().decode(FrameworkInfo.self, from: smallData(frameworkInfoURL))
        guard frameworkInfo.CFBundleExecutable == frameworkName, frameworkInfo.CFBundlePackageType == "FMWK",
              frameworkInfo.CFBundleSupportedPlatforms == [simulator ? "iPhoneSimulator" : "iPhoneOS"],
              frameworkInfo.DTPlatformName == (simulator ? "iphonesimulator" : "iphoneos"),
              frameworkInfo.MinimumOSVersion == "17.0" else {
            throw VerificationError(message: "Framework metadata must describe iOS 17.0 and the selected platform")
        }
    }
}

private func verify(root: URL) throws {
    let lockURL = try checkedURL(root: root, relative: "terminal-core.ios.lock.json", file: true)
    let lock = try JSONDecoder().decode(NativeLock.self, from: smallData(lockURL))
    guard lock.schema_version == 2, lock.repository == "git@github.com:myucxy/terminal-core.git",
          lock.toolchain == "1.97.1", !lock.source_dirty,
          matches(lock.revision, "^[0-9a-f]{40}$"), !lock.artifacts.isEmpty, lock.artifacts.count <= 10_000 else {
        throw VerificationError(message: "Invalid independent iOS native lock provenance")
    }
    _ = try checkedURL(root: root, relative: frameworkRelative, file: false)
    var recorded = Set<String>(), canonicalNames = Set<String>()
    for artifact in lock.artifacts {
        _ = try components(artifact.path)
        guard artifact.platform == "ios", artifact.path.hasPrefix(frameworkRelative + "/"),
              matches(artifact.sha256, "^[0-9a-f]{64}$"), recorded.insert(artifact.path).inserted,
              canonicalNames.insert(artifact.path.lowercased()).inserted else {
            throw VerificationError(message: "Invalid, duplicate or non-iOS artifact record")
        }
        let url = try checkedURL(root: root, relative: artifact.path, file: true)
        try verifyDigest(artifact, at: url)
    }
    guard try inventory(root: root, relative: frameworkRelative) == recorded else {
        throw VerificationError(message: "XCFramework files and the iOS lock do not cover each other exactly")
    }
    try verifyFrameworkLayout(root: root)
    print("Verified iOS terminal-core revision \(lock.revision)")
}

do {
    let arguments = Array(CommandLine.arguments.dropFirst())
    let root: URL
    if arguments.isEmpty {
        let script = URL(fileURLWithPath: #filePath).resolvingSymlinksInPath()
        root = script.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    } else if arguments.count == 2, arguments[0] == "--root", arguments[1].hasPrefix("/") {
        root = URL(fileURLWithPath: arguments[1], isDirectory: true).standardizedFileURL.resolvingSymlinksInPath()
    } else {
        throw VerificationError(message: "Usage: verify_native.swift [--root /absolute/application/root]")
    }
    try verify(root: root)
} catch {
    fputs("error: iOS native artifact verification failed: \(error.localizedDescription)\n", stderr)
    exit(1)
}
