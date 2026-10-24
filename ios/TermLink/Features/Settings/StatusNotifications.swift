// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import UserNotifications

@MainActor final class StatusNotifications: NSObject, UNUserNotificationCenterDelegate {
    var onOpen: ((UUID, String) -> Void)?
    private var seen: [String: String] = [:]
    override init() { super.init(); UNUserNotificationCenter.current().delegate = self }
    func isEnabled() async -> Bool {
        let settings = await UNUserNotificationCenter.current().notificationSettings()
        return UserDefaults.standard.bool(forKey: "notifications-enabled") && [.authorized, .provisional, .ephemeral].contains(settings.authorizationStatus)
    }
    func setEnabled(_ enabled: Bool) async throws -> Bool {
        let allowed = enabled ? try await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) : false
        UserDefaults.standard.set(allowed, forKey: "notifications-enabled")
        if !allowed { UNUserNotificationCenter.current().removeAllDeliveredNotifications() }
        return allowed
    }
    func observe(_ session: AIConversation) {
        let next = session.pending.isEmpty ? session.state : "needs-input"
        let previous = seen.updateValue(next, forKey: session.id)
        guard let previous, previous != next, next == "needs-input" || previous == "running" && next == "idle",
              UserDefaults.standard.bool(forKey: "notifications-enabled") else { return }
        let content = UNMutableNotificationContent()
        content.title = "TermLink"
        content.body = next == "needs-input" ? "AI 会话需要确认。" : "AI 会话已完成回复。"
        content.userInfo = ["pc": session.pcID.uuidString, "session": session.id]
        if UserDefaults.standard.bool(forKey: "notification-sound") { content.sound = .default }
        UNUserNotificationCenter.current().add(.init(identifier: session.id, content: content, trigger: nil)) { _ in }
    }
    func reset() { seen = [:]; UNUserNotificationCenter.current().removeAllDeliveredNotifications(); UNUserNotificationCenter.current().removeAllPendingNotificationRequests() }
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        notification.request.content.sound == nil ? [.banner, .list] : [.banner, .list, .sound]
    }
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        let info = response.notification.request.content.userInfo
        guard let text = info["pc"] as? String, let pc = UUID(uuidString: text), let session = info["session"] as? String else { return }
        await MainActor.run { self.onOpen?(pc, session) }
    }
}
