// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/** Durable envelope for the locally persisted native snapshot. `ack` and `localInputEpoch`
 *  are the resume fences these exact bytes were validated at, so they can never diverge
 *  from the snapshot contents. Restoring them today is display-only: the host snapshot
 *  stays authoritative because the host may hold a younger or rewound event sequence
 *  than this device. */
struct PersistedTerminalSnapshot: Codable {
    var version: Int
    var ack: UInt64
    var localInputEpoch: UInt64
    var snapshot: Data
    static func storageName(_ session: UUID) -> String { "terminal:" + session.uuidString }
}
