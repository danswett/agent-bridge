import AgentBridgeAppleCore
import Foundation
import Observation
import UserNotifications
#if os(macOS)
    import AppKit
#endif

@MainActor @Observable
public final class BridgeNotificationManager: NSObject, UNUserNotificationCenterDelegate {
    public private(set) var enabled: Bool
    public private(set) var waitingCount = 0
    public private(set) var isRequestingPermission = false
    public private(set) var status = "Notifications are off."
    public var quiet: Bool {
        didSet {
            defaults.set(quiet, forKey: "bridge.notifications.quiet")
            if quiet {
                removePending()
            }
        }
    }

    public var showPreviews: Bool {
        didSet {
            defaults.set(showPreviews, forKey: "bridge.notifications.previews")
            // Turning previews off also removes text already left in Notification Center.
            if !showPreviews {
                removeAllNotices()
            }
        }
    }

    public var onSelectSession: (@MainActor (String, String) -> Void)?
    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored private let center: UNUserNotificationCenter
    @ObservationIgnored private var sessions: [String: [String: BridgeSession]] = [:]
    @ObservationIgnored private var knownNotices: [String: BridgeSessionNotice] = [:]
    @ObservationIgnored private var foreground = false
    @ObservationIgnored private var generation = UUID()
    @ObservationIgnored private var permissionGeneration = UUID()

    public init(defaults: UserDefaults = .standard, center: UNUserNotificationCenter = .current()) {
        self.defaults = defaults
        self.center = center
        enabled = defaults.bool(forKey: "bridge.notifications.enabled")
        quiet = defaults.bool(forKey: "bridge.notifications.quiet")
        showPreviews = defaults.bool(forKey: "bridge.notifications.previews")
        super.init()
        center.delegate = self
        center.removeAllPendingNotificationRequests()
        center.removeAllDeliveredNotifications()
    }

    /// Call only from the person's Enable Notifications control. Initialization,
    /// connection and session updates never request system permission.
    public func setEnabled(_ value: Bool) async {
        permissionGeneration = UUID()
        let token = permissionGeneration
        if !value {
            enabled = false
            isRequestingPermission = false
            defaults.set(false, forKey: "bridge.notifications.enabled")
            status = "Notifications are off."
            removeAllNotices()
            updateBadge()
            return
        }
        isRequestingPermission = true
        defer {
            if token == permissionGeneration {
                isRequestingPermission = false
            }
        }
        do {
            let settings = await center.notificationSettings()
            guard token == permissionGeneration else { return }
            let authorized: Bool = if settings.authorizationStatus == .notDetermined {
                try await center.requestAuthorization(options: [.alert, .sound, .badge])
            } else {
                settings.authorizationStatus == .authorized || settings.authorizationStatus == .provisional
            }
            guard token == permissionGeneration else { return }
            enabled = authorized
            defaults.set(authorized, forKey: "bridge.notifications.enabled")
            status = authorized ? "Notifications are on." : "Allow notifications for Agent Bridge in System Settings to turn them on."
            updateBadge()
        } catch {
            guard token == permissionGeneration else { return }
            enabled = false
            defaults.set(false, forKey: "bridge.notifications.enabled")
            status = "Could not enable notifications: \(error.localizedDescription)"
            updateBadge()
        }
    }

    public func refreshAuthorization() async {
        let token = permissionGeneration
        let settings = await center.notificationSettings()
        guard token == permissionGeneration else { return }
        if enabled, settings.authorizationStatus != .authorized, settings.authorizationStatus != .provisional {
            enabled = false
            defaults.set(false, forKey: "bridge.notifications.enabled")
            status = "Notifications are disabled in System Settings."
            removeAllNotices()
        } else {
            status = enabled ? "Notifications are on." : "Notifications are off."
        }
        updateBadge()
    }

    public func setForeground(_ value: Bool) {
        foreground = value
        #if os(iOS)
            if !value {
                removePending()
            }
        #endif
    }

    public func update(machineID: String, machineName: String, before: [BridgeSession], after: [BridgeSession], initialSnapshot: Bool = false) {
        sessions[machineID] = Dictionary(after.map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
        let plan = BridgeNotificationPlanner.plan(machineID: machineID, machineName: machineName, before: before, after: after,
                                                  showPreviews: showPreviews, initialSnapshot: initialSnapshot)
        let stale = knownNotices.values.filter { $0.machineID == machineID && !BridgeNotificationPlanner.isCurrent($0, session: sessions[machineID]?[$0.sessionID]) }.map(\.id)
        remove(Array(Set(plan.removeIDs + stale)))
        waitingCount = sessions.values.reduce(0) { $0 + $1.values.filter { BridgeNotificationPlanner.pendingQuestion($0) != nil }.count }
        updateBadge()
        guard enabled, !quiet else { return }
        for notice in plan.notices where knownNotices[notice.id] == nil {
            knownNotices[notice.id] = notice
            let token = generation
            Task { await deliver(notice, generation: token) }
        }
    }

    public func clear(machineID: String, sessionID: String? = nil) {
        let ids = knownNotices.values.filter { $0.machineID == machineID && (sessionID == nil || $0.sessionID == sessionID) }.map(\.id)
        remove(ids)
        if let sessionID {
            sessions[machineID]?.removeValue(forKey: sessionID)
        } else {
            sessions.removeValue(forKey: machineID)
        }
        waitingCount = sessions.values.reduce(0) { $0 + $1.values.filter { BridgeNotificationPlanner.pendingQuestion($0) != nil }.count }
        updateBadge()
    }

    public func clearAll() {
        generation = UUID()
        sessions = [:]
        waitingCount = 0
        removeAllNotices()
        updateBadge()
    }

    private func canDeliver(_ notice: BridgeSessionNotice, generation token: UUID) -> Bool {
        guard token == generation, enabled, !quiet, knownNotices[notice.id] != nil,
              BridgeNotificationPlanner.isCurrent(notice, session: sessions[notice.machineID]?[notice.sessionID]) else { return false }
        #if os(iOS)
            return foreground
        #else
            return true
        #endif
    }

    private func deliver(_ notice: BridgeSessionNotice, generation token: UUID) async {
        guard canDeliver(notice, generation: token) else { knownNotices.removeValue(forKey: notice.id); return }
        let content = UNMutableNotificationContent()
        content.title = notice.title
        content.body = notice.body
        content.sound = .default
        content.threadIdentifier = "agentbridge.sessions"
        content.userInfo = ["machine": notice.machineID, "session": notice.sessionID]
        do {
            try await center.add(UNNotificationRequest(identifier: notice.id, content: content, trigger: nil))
            if !canDeliver(notice, generation: token) {
                remove([notice.id])
            }
        } catch {
            knownNotices.removeValue(forKey: notice.id)
            status = "Could not show a notification: \(error.localizedDescription)"
        }
    }

    private func remove(_ identifiers: [String]) {
        guard !identifiers.isEmpty else { return }
        center.removePendingNotificationRequests(withIdentifiers: identifiers)
        center.removeDeliveredNotifications(withIdentifiers: identifiers)
        for id in identifiers {
            knownNotices.removeValue(forKey: id)
        }
    }

    private func removePending() {
        generation = UUID()
        center.removePendingNotificationRequests(withIdentifiers: Array(knownNotices.keys))
    }

    private func removeAllNotices() {
        generation = UUID()
        remove(Array(knownNotices.keys))
        // This center belongs to the application; clear leftovers from its previous run.
        center.removeAllPendingNotificationRequests()
        center.removeAllDeliveredNotifications()
    }

    private func updateBadge() {
        #if os(macOS)
            NSApplication.shared.dockTile.badgeLabel = enabled && waitingCount > 0 ? String(waitingCount) : nil
        #endif
    }

    public nonisolated func userNotificationCenter(_: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        let info = response.notification.request.content.userInfo
        let machine = info["machine"] as? String
        let session = info["session"] as? String
        let isDefault = response.actionIdentifier == UNNotificationDefaultActionIdentifier
        await MainActor.run { [weak self] in
            if isDefault, let machine, let session {
                self?.onSelectSession?(machine, session)
            }
        }
    }

    public nonisolated func userNotificationCenter(_: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        let identifier = notification.request.identifier
        return await MainActor.run { [weak self] in
            guard let self, let notice = knownNotices[identifier], canDeliver(notice, generation: generation) else { return [] }
            return [.banner, .list, .sound]
        }
    }
}
