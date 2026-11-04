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

    public private(set) var actionStatus: String?
    public var onAction: (@MainActor (BridgeNoticeActionRequest) async -> String)?
    public func dismissActionStatus() {
        actionStatus = nil
    }

    public var onSelectSession: (@MainActor (String, String) -> Void)?
    public var notifyFinished: Bool {
        didSet { defaults.set(notifyFinished, forKey: "bridge.notifications.finished") }
    }

    @ObservationIgnored private var focusedSession: (machine: String, session: String)?
    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored private let center: UNUserNotificationCenter
    @ObservationIgnored private var knownNotices: [String: BridgeSessionNotice] = [:]
    @ObservationIgnored private var deliveryIdentifiers: [String: String] = [:]
    @ObservationIgnored private var actionRegistry = BridgeNoticeActionRegistry()
    @ObservationIgnored private var history = BridgeNotificationPlanner.History()
    @ObservationIgnored private var foreground = false
    @ObservationIgnored private var generation = UUID()
    @ObservationIgnored private var permissionGeneration = UUID()

    public init(defaults: UserDefaults = .standard, center: UNUserNotificationCenter = .current()) {
        self.defaults = defaults
        self.center = center
        enabled = defaults.bool(forKey: "bridge.notifications.enabled")
        quiet = defaults.bool(forKey: "bridge.notifications.quiet")
        showPreviews = defaults.bool(forKey: "bridge.notifications.previews")
        notifyFinished = defaults.object(forKey: "bridge.notifications.finished") as? Bool ?? true
        super.init()
        center.delegate = self
        center.setNotificationCategories([])
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
            let authorization = await authorizationStatus()
            guard token == permissionGeneration else { return }
            let authorized: Bool = if authorization == .notDetermined {
                try await requestAuthorization()
            } else {
                authorization == .authorized || authorization == .provisional
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
        let authorization = await authorizationStatus()
        guard token == permissionGeneration else { return }
        if enabled, authorization != .authorized, authorization != .provisional {
            enabled = false
            defaults.set(false, forKey: "bridge.notifications.enabled")
            status = "Notifications are disabled in System Settings."
            removeAllNotices()
        } else {
            status = enabled ? "Notifications are on." : "Notifications are off."
        }
        updateBadge()
    }

    private func authorizationStatus() async -> UNAuthorizationStatus {
        // Older SDKs do not make UNNotificationSettings Sendable. Extract its
        // scalar inside the callback instead of moving the object across actors.
        let raw: Int = await withCheckedContinuation { continuation in
            center.getNotificationSettings { settings in
                continuation.resume(returning: settings.authorizationStatus.rawValue)
            }
        }
        return UNAuthorizationStatus(rawValue: raw) ?? .denied
    }

    private func requestAuthorization() async throws -> Bool {
        try await withCheckedThrowingContinuation { continuation in
            center.requestAuthorization(options: [.alert, .sound, .badge]) { granted, error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    continuation.resume(returning: granted)
                }
            }
        }
    }

    private func submit(_ request: UNNotificationRequest) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            center.add(request) { error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    continuation.resume()
                }
            }
        }
    }

    public func setForeground(_ value: Bool) {
        foreground = value
        #if os(iOS)
            if !value {
                removePending()
            }
        #endif
    }

    public func focus(machineID: String?, sessionID: String?) {
        if let machineID, let sessionID {
            focusedSession = (machineID, sessionID)
        } else {
            focusedSession = nil
        }
    }

    public func update(machineID: String, machineName: String, before _: [BridgeSession], after: [BridgeSession], initialSnapshot: Bool = false, capabilities: Set<String> = [], scope: String = "local") {
        let plan = history.plan(machineID: machineID, machineName: machineName, after: after,
                                showPreviews: showPreviews, initialSnapshot: initialSnapshot, capabilities: capabilities)
        let stale = knownNotices.values.filter { $0.machineID == machineID && !BridgeNotificationPlanner.isCurrent($0, session: history.sessions[machineID]?[$0.sessionID]) }.map(\.id)
        remove(Array(Set(plan.removeIDs + stale)))
        waitingCount = history.sessions.values.reduce(0) { $0 + $1.values.filter { BridgeNotificationPlanner.pendingQuestion($0) != nil }.count }
        updateBadge()
        guard enabled, !quiet else { return }
        for notice in plan.notices where knownNotices[notice.id] == nil {
            knownNotices[notice.id] = notice
            actionRegistry.register(notice, scope: scope)
            let token = generation
            let identifier = notice.id + "." + UUID().uuidString
            deliveryIdentifiers[notice.id] = identifier
            Task { await deliver(notice, generation: token, identifier: identifier) }
        }
    }

    public func clear(machineID: String, sessionID: String? = nil) {
        history.clear(machineID: machineID, sessionID: sessionID)
        let ids = knownNotices.values.filter { $0.machineID == machineID && (sessionID == nil || $0.sessionID == sessionID) }.map(\.id)
        remove(ids)
        waitingCount = history.sessions.values.reduce(0) { $0 + $1.values.filter { BridgeNotificationPlanner.pendingQuestion($0) != nil }.count }
        updateBadge()
    }

    public func clearAll() {
        actionStatus = nil
        generation = UUID()
        history = BridgeNotificationPlanner.History()
        waitingCount = 0
        removeAllNotices()
        updateBadge()
    }

    private func canDeliver(_ notice: BridgeSessionNotice, generation token: UUID) -> Bool {
        guard token == generation, enabled, !quiet, knownNotices[notice.id] != nil,
              BridgeNotificationPlanner.isCurrent(notice, session: history.sessions[notice.machineID]?[notice.sessionID]) else { return false }
        if notice.kind == .finished, !notifyFinished {
            return false
        }
        if foreground, focusedSession?.machine == notice.machineID, focusedSession?.session == notice.sessionID {
            #if os(macOS)
                if NSApplication.shared.isActive, NSApplication.shared.keyWindow != nil {
                    return false
                }
            #else
                return false
            #endif
        }
        #if os(iOS)
            return foreground
        #else
            return true
        #endif
    }

    private func deliver(_ notice: BridgeSessionNotice, generation token: UUID, identifier: String) async {
        do { try await Task.sleep(for: .seconds(notice.kind == .question ? 2 : 3)) } catch { return }
        guard deliveryIdentifiers[notice.id] == identifier else { return }
        guard canDeliver(notice, generation: token) else { removeDelivery(notice.id, identifier: identifier); return }
        let content = UNMutableNotificationContent()
        content.title = notice.title
        content.body = notice.body
        content.sound = .default
        content.threadIdentifier = "agentbridge.sessions"
        content.userInfo = ["machine": notice.machineID, "session": notice.sessionID, "notice": notice.id]
        if let binding = actionRegistry.bindings[notice.id] {
            content.userInfo["actionToken"] = binding.token.uuidString
            content.categoryIdentifier = "agentbridge.actions." + binding.token.uuidString
            refreshCategories()
        }
        do {
            try await submit(UNNotificationRequest(identifier: identifier, content: content, trigger: nil))
            if deliveryIdentifiers[notice.id] != identifier || !canDeliver(notice, generation: token) {
                removeDelivery(notice.id, identifier: identifier)
            }
        } catch {
            let isCurrent = deliveryIdentifiers[notice.id] == identifier
            removeDelivery(notice.id, identifier: identifier)
            if isCurrent {
                status = "Could not show a notification: \(error.localizedDescription)"
            }
        }
    }

    private func remove(_ identifiers: [String]) {
        guard !identifiers.isEmpty else { return }
        let delivered = identifiers.compactMap { deliveryIdentifiers[$0] }
        center.removePendingNotificationRequests(withIdentifiers: delivered)
        center.removeDeliveredNotifications(withIdentifiers: delivered)
        for id in identifiers {
            knownNotices.removeValue(forKey: id)
            actionRegistry.remove(id)
            deliveryIdentifiers.removeValue(forKey: id)
        }
        refreshCategories()
    }

    private func removeDelivery(_ noticeID: String, identifier: String) {
        if deliveryIdentifiers[noticeID] == identifier {
            remove([noticeID])
        } else {
            // An old add can complete after the same question was re-registered.
            // Its unique system ID cannot remove or overwrite the new notice.
            center.removePendingNotificationRequests(withIdentifiers: [identifier])
            center.removeDeliveredNotifications(withIdentifiers: [identifier])
        }
    }

    private func refreshCategories() {
        center.setNotificationCategories(Set(actionRegistry.bindings.values.map { Self.category(notice: $0.notice, token: $0.token) }))
    }

    static func category(notice: BridgeSessionNotice, token: UUID) -> UNNotificationCategory {
        let actions: [UNNotificationAction] = notice.actions.map { action in
            switch action.kind {
            case .choice:
                UNNotificationAction(identifier: action.id, title: action.title, options: [.foreground, .authenticationRequired])
            case .answerText, .reply:
                UNTextInputNotificationAction(identifier: action.id, title: action.title, options: [.foreground, .authenticationRequired],
                                              textInputButtonTitle: "Send", textInputPlaceholder: action.kind == .reply ? "Message" : "Answer")
            }
        }
        return UNNotificationCategory(identifier: "agentbridge.actions." + token.uuidString, actions: actions, intentIdentifiers: [], options: [])
    }

    private func removePending() {
        generation = UUID()
        center.removePendingNotificationRequests(withIdentifiers: Array(deliveryIdentifiers.values))
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
        let identifier = info["notice"] as? String ?? response.notification.request.identifier
        let machine = info["machine"] as? String
        let session = info["session"] as? String
        let actionToken = info["actionToken"] as? String
        let action = response.actionIdentifier
        let text = (response as? UNTextInputNotificationResponse)?.userText
        await handleResponse(identifier: identifier, machine: machine, session: session, actionToken: actionToken, action: action, text: text)
    }

    private func handleResponse(identifier: String, machine: String?, session: String?, actionToken: String?, action: String, text: String?) async {
        guard action != UNNotificationDismissActionIdentifier else { return }
        if let machine, let session {
            onSelectSession?(machine, session)
        }
        guard action != UNNotificationDefaultActionIdentifier else { return }
        guard enabled, let binding = actionRegistry.bindings[identifier], actionToken == binding.token.uuidString,
              binding.notice.machineID == machine, binding.notice.sessionID == session,
              knownNotices[identifier] == binding.notice,
              BridgeNotificationPlanner.isCurrent(binding.notice, session: history.sessions[binding.notice.machineID]?[binding.notice.sessionID]),
              let onAction,
              let request = actionRegistry.consume(identifier: identifier, token: actionToken, machine: machine, session: session, action: action, text: text)
        else {
            actionStatus = "Not sent. This notification expired or the machine is offline. Review the session."
            return
        }
        // Consume before the first suspension: duplicate OS callbacks cannot create
        // a second press, even if the host's result arrives before its next upsert.
        remove([identifier])
        let token = generation
        actionStatus = "Checking the current session…"
        let result = await onAction(request)
        guard token == generation else { return }
        actionStatus = result
    }

    public nonisolated func userNotificationCenter(_: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        let identifier = notification.request.identifier
        let noticeID = notification.request.content.userInfo["notice"] as? String
        return await MainActor.run { [weak self] in
            guard let self, let noticeID, deliveryIdentifiers[noticeID] == identifier,
                  let notice = knownNotices[noticeID], canDeliver(notice, generation: generation) else { return [] }
            return [.banner, .list, .sound]
        }
    }
}
