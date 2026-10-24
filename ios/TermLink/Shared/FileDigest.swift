// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import Foundation

enum FileDigest {
    static func sha256(_ url: URL, size: UInt64, limit: UInt64) async throws -> Data {
        guard size <= limit else { throw ClientError.limitExceeded }
        let task = Task.detached(priority: .utility) {
            try Task.checkCancellation()
            let file = try FileHandle(forReadingFrom: url); defer { try? file.close() }
            var hash = SHA256(), read: UInt64 = 0
            while let bytes = try file.read(upToCount: 65536), !bytes.isEmpty {
                try Task.checkCancellation()
                guard UInt64(bytes.count) <= size-read else { throw ClientError.invalidResponse("file changed while reading") }
                read += UInt64(bytes.count); hash.update(data: bytes)
            }
            guard read == size else { throw ClientError.invalidResponse("file length changed") }
            try Task.checkCancellation()
            return Data(hash.finalize())
        }
        return try await withTaskCancellationHandler(operation: { try await task.value }, onCancel: { task.cancel() })
    }
}
