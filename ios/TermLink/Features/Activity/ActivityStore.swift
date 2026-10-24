// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// Local device activity feed, mirroring the Android 活动页: this is the phone's own
/// history of control and session events, not a server audit log.
struct ActivityEntry: Codable, Identifiable, Equatable {
    var id: UUID
    var time: Date
    var kind: String
    var detail: String
}

@MainActor final class ActivityStore: ObservableObject {
    static let cap = 200
    @Published private(set) var entries: [ActivityEntry] = []
    private var loaded = false
    weak var product: ProductStore?
    func restoreIfNeeded() {
        guard !loaded else { return }; loaded = true
        entries = (try? product?.privateState.load("activity", as: [ActivityEntry].self)) ?? []
    }
    func record(_ kind: String, _ detail: String) {
        restoreIfNeeded()
        entries.insert(ActivityEntry(id: UUID(), time: Date(), kind: kind, detail: detail), at: 0)
        if entries.count > Self.cap { entries.removeLast(entries.count - Self.cap) }
        try? product?.privateState.save(entries, name: "activity")
    }
    func clear() { entries = []; loaded = true; try? product?.privateState.save(entries, name: "activity") }
}
