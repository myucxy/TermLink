// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// Mirrors Android's bounded lease contention/transient retry policy.
/// This never force-releases another controller's lease.
@MainActor enum TerminalControlAcquisition {
    static let heldDelays: [Duration] = [.seconds(1), .seconds(2), .seconds(4), .seconds(8), .seconds(15), .seconds(5)]
    static let transientDelays: [Duration] = [.milliseconds(500), .seconds(1), .seconds(2)]

    static func acquire(current: () -> Bool, controlling: () -> Bool,
                        request: () async throws -> Void,
                        pause: (Duration) async throws -> Void = { try await Task.sleep(for: $0) },
                        retrying: () -> Void = {}) async throws {
        var held = 0, transient = 0
        while true {
            try Task.checkCancellation()
            guard current() else { throw CancellationError() }
            if controlling() { return }
            do { try await request(); return }
            catch {
                try Task.checkCancellation()
                guard current() else { throw CancellationError() }
                let delay: Duration
                if let api = error as? APIError, api.status == 409, api.code == "LEASE_HELD", held < heldDelays.count {
                    delay = heldDelays[held]; held += 1
                } else if transientFailure(error), transient < transientDelays.count {
                    delay = transientDelays[transient]; transient += 1
                } else { throw error }
                retrying()
                try await pause(delay)
            }
        }
    }
    private static func transientFailure(_ error: Error) -> Bool {
        if let api = error as? APIError { return [408, 425, 429].contains(api.status) || (500...599).contains(api.status) }
        guard let network = error as? URLError else { return false }
        return [.timedOut, .networkConnectionLost, .cannotFindHost, .cannotConnectToHost, .dnsLookupFailed, .notConnectedToInternet].contains(network.code)
    }
}

struct TerminalLeaseGrant {
    let id: UUID, version: UInt64, expires: Date
    static func decode(_ value: JSONValue, now: Date = Date()) throws -> Self {
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        guard let id = UUID(uuidString: value["lease_id"].string), id.bytes.contains(where: { $0 != 0 }),
              value["version"].integer > 0,
              let expiry = formatter.date(from: value["expires_at"].string) ?? ISO8601DateFormatter().date(from: value["expires_at"].string), expiry > now else {
            throw ClientError.protocolViolation("control lease")
        }
        return .init(id: id, version: UInt64(value["version"].integer), expires: expiry)
    }
}

/// Selection order is the order in which local views were opened, never Dictionary iteration order.
struct CLISessionTabs: Equatable {
    private(set) var ids: [UUID] = []
    private(set) var selected: UUID?
    mutating func open(_ id: UUID) {
        if !ids.contains(id) { ids.append(id) }
        selected = id
    }
    mutating func select(_ id: UUID?) { if let id, ids.contains(id) { selected = id } }
    mutating func close(_ id: UUID) {
        guard let index = ids.firstIndex(of: id) else { return }
        ids.remove(at: index)
        if selected == id { selected = ids.isEmpty ? nil : ids[min(index, ids.count - 1)] }
    }
}
