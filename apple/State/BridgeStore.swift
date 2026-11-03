import AgentBridgeAppleCore
import Foundation
import Observation

@MainActor
struct SessionEntry: Identifiable {
    nonisolated let id: String
    let session: BridgeSession
    let host: HostStore?
    var ended = false
    var machineName: String {
        host?.name ?? "Sample workspace"
    }

    init(session: BridgeSession, host: HostStore?, ended: Bool = false) {
        id = "\(host?.hello?["machine"]?["id"]?.string ?? host?.id ?? "sample"):\(session.id)"
        self.session = session
        self.host = host
        self.ended = ended
    }
}

@MainActor @Observable
final class BridgeStore {
    private(set) var hosts: [HostStore] = []
    private(set) var roster: [RelayMachine] = []
    private(set) var remoteStatus = "Not signed in"
    private(set) var account: RelayAccount?
    private(set) var signInCode: RelayDeviceCode?
    private(set) var signInStatus: String?
    private(set) var isSigningIn = false
    private(set) var isSample = false
    private(set) var isFixture = false
    private(set) var cachedUsage: JSONValue?
    private(set) var cacheStatus: String?
    var onOpenSession: (() -> Void)?
    @ObservationIgnored lazy var notifications = BridgeNotificationManager()
    @ObservationIgnored private let usageCache = BridgeUsageCache()
    @ObservationIgnored private lazy var usageLifecycle = BridgeUsageLifecycle(cache: usageCache)
    @ObservationIgnored private var cacheSession: BridgeUsageCacheSession?
    @ObservationIgnored private var hasRosterSnapshot = false
    @ObservationIgnored private var usageRoster = Set<String>()
    @ObservationIgnored private var usageRosterRevision = 0
    @ObservationIgnored private var pendingSelection: (machine: String, session: String)?
    private(set) var activeOrigin: String?
    private(set) var activeAccountID: String?
    var profile = UserDefaults.standard.string(forKey: "bridgeProfile") ?? "default"
    var relayAddress = UserDefaults.standard.string(forKey: "bridgeRelay") ?? "https://relay.bad-duck.com"
    var deviceName = UserDefaults.standard.string(forKey: "bridgeDeviceName") ?? "Apple client"
    var selectedMachine: String?
    var selectedSessionID: String?
    var launchDrafts: [String: BridgeLaunchDraft] = [:]
    private(set) var hasStarted = false
    var sortOrder = UserDefaults.standard.string(forKey: "bridgeSort") ?? "manual" {
        didSet { UserDefaults.standard.set(sortOrder, forKey: "bridgeSort") }
    }

    private(set) var pinned: Set<String> = []
    private(set) var manualOrder: [String] = []
    private(set) var readVersions: [String: String] = [:]

    @ObservationIgnored private var relay: RelayBridge?
    @ObservationIgnored private var relayTask: Task<Void, Never>?
    @ObservationIgnored private var signInTask: Task<Void, Never>?
    @ObservationIgnored private var generation = UUID()
    @ObservationIgnored private var signInGeneration = UUID()
    @ObservationIgnored private var canPollGitHub = true
    @ObservationIgnored private var hostRelays: [String: ObjectIdentifier] = [:]

    var isLive: Bool {
        hosts.contains { $0.isLive }
    }

    var isConnecting: Bool {
        hosts.contains { $0.isConnecting }
    }

    var entries: [SessionEntry] {
        if isSample {
            return SampleSessions.sessions.map { SessionEntry(session: $0, host: nil) }
        }
        return canonicalHosts.flatMap { host in host.sessions.map { SessionEntry(session: $0, host: host) } }
    }

    var canonicalHosts: [HostStore] {
        let groups = Dictionary(grouping: hosts) { $0.hello?["machine"]?["id"]?.string ?? $0.id }
        return groups.values.compactMap { group in
            group.sorted {
                if $0.isLive != $1.isLive {
                    return $0.isLive
                }
                if $0.isLocal != $1.isLocal {
                    return $0.isLocal
                }
                return $0.id < $1.id
            }.first
        }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    func includesSelectedMachine(_ host: HostStore?) -> Bool {
        guard let selectedMachine else { return true }
        guard let host else { return false }
        if host.id == selectedMachine {
            return true
        }
        guard let selected = hosts.first(where: { $0.id == selectedMachine }),
              let machineID = selected.hello?["machine"]?["id"]?.string else { return false }
        return host.hello?["machine"]?["id"]?.string == machineID
    }

    var endedEntries: [SessionEntry] {
        canonicalHosts.flatMap { host in host.endedSessions.map { SessionEntry(session: $0, host: host, ended: true) } }
    }

    var sessions: [BridgeSession] {
        entries.map(\.session)
    }

    var connectionStatus: String {
        if isFixture {
            return "Demo fixture · simulated actions · no live hosts"
        }
        if isSample {
            return "Sample sessions · not connected"
        }
        let connected = canonicalHosts.filter(\.isLive).count
        if connected > 0 {
            return "\(connected) \(connected == 1 ? "machine" : "machines") connected · \(entries.count) sessions"
        }
        return hosts.first?.status ?? remoteStatus
    }

    private var preferenceScope: String {
        "bridge.\(activeOrigin ?? "local").\(activeAccountID ?? "local")"
    }

    func startOnce() {
        guard !hasStarted else { return }
        hasStarted = true
        notifications.onSelectSession = { [weak self] machineID, sessionID in
            guard let self else { return }
            pendingSelection = (machineID, sessionID)
            resolvePendingSelection()
            onOpenSession?()
        }
        notifications.onAction = { [weak self] request in
            guard let self, request.scope == preferenceScope,
                  let host = canonicalHosts.first(where: { ($0.hello?["machine"]?["id"]?.string ?? $0.id) == request.notice.machineID })
            else {
                return "Not sent. This account or machine is no longer connected. Review the session."
            }
            let token = generation
            return await BridgeNotificationDelivery.perform(request, host: host, scope: preferenceScope) { [self] in
                token == generation && request.scope == preferenceScope && hosts.contains(where: { $0 === host })
            }
        }
        Task { await notifications.refreshAuthorization() }
        #if DEBUG
            if ProcessInfo.processInfo.arguments.contains("--ui-fixture") {
                isFixture = true
                let fixture = PreviewHost()
                let host = HostStore(id: "preview", name: "Demo machine", isLocal: false) { await fixture.connect() }
                hosts = [host]
                host.start()
                selectedSessionID = "preview:preview-review"
                return
            }
        #endif
        if ProcessInfo.processInfo.arguments.contains("--sample-sessions") {
            showSamples()
            selectedSessionID = entries.first?.id
        } else {
            connect()
        }
    }

    func connect() {
        guard !isFixture else { return }
        let previousScope = preferenceScope
        disconnect()
        isSample = false
        isFixture = false
        hasRosterSnapshot = false
        loadPreferences()
        #if os(macOS)
            let chosenProfile = profile
            UserDefaults.standard.set(profile, forKey: "bridgeProfile")
            let localID = "local:\(profile)"
            hosts.filter { $0.isLocal && $0.id != localID }.forEach { $0.stop() }
            hosts.removeAll { $0.isLocal && $0.id != localID }
            let local = hosts.first { $0.id == localID } ?? HostStore(id: localID, name: "This Mac", isLocal: true) {
                let transport = LocalBridge()
                let stream = try await transport.connect(path: LocalEndpoint.socketPath(profile: chosenProfile))
                return HostTransport(messages: stream, send: { try await transport.request($0, timeout: $1) },
                                     close: { await transport.disconnect() }, peerKey: nil)
            }
            if !hosts.contains(where: { $0.id == localID }) {
                hosts.append(local)
            }
            observe(local)
            local.start()
        #endif
        do {
            relayAddress = try RelayAddress.validated(relayAddress).absoluteString
            UserDefaults.standard.set(relayAddress, forKey: "bridgeRelay")
            UserDefaults.standard.set(deviceName, forKey: "bridgeDeviceName")
            if let data = try BridgeKeychain.load("account:\(relayAddress)") {
                account = try JSONDecoder().decode(RelayAccount.self, from: data)
            } else {
                account = nil
            }
            activeOrigin = relayAddress
            activeAccountID = account?.accountId
            if previousScope != preferenceScope {
                launchDrafts = [:]
                pendingSelection = nil
                notifications.clearAll()
                cachedUsage = nil
                cacheSession = nil
                hosts.removeAll { !$0.isLocal }
                roster = []
                hostRelays = [:]
                selectedMachine = nil
            }
            loadPreferences()
            activateUsageCache()
            if let account {
                startRelay(account)
            } else {
                remoteStatus = "Sign in to connect your other machines"
            }
        } catch {
            account = nil
            activeAccountID = nil
            cachedUsage = nil
            cacheSession = nil
            cacheStatus = nil
            notifications.clearAll()
            usageLifecycle.deactivate()
            launchDrafts = [:]
            hosts.removeAll { !$0.isLocal }
            roster = []
            remoteStatus = error.localizedDescription
        }
    }

    func disconnect() {
        generation = UUID()
        relayTask?.cancel()
        relayTask = nil
        let old = relay
        relay = nil
        if let old {
            Task { await old.disconnect() }
        }
        hosts.forEach { $0.stop() }
        remoteStatus = "Disconnected"
    }

    func suspend() {
        canPollGitHub = false
        notifications.setForeground(false)
        if !isSample, !isFixture {
            disconnect()
        }
    }

    func resume() {
        canPollGitHub = true
        notifications.setForeground(true)
        if !isSample, !isFixture {
            connect()
        }
    }

    func signIn() {
        guard !isFixture else { return }
        cancelSignIn()
        let token = signInGeneration
        isSigningIn = true
        signInStatus = "Requesting a sign-in code…"
        let address = relayAddress
        signInTask = Task {
            do {
                let address = try RelayAddress.validated(address).absoluteString
                let flow = try RelaySignIn(relay: address)
                let code = try await flow.start()
                guard token == signInGeneration else { return }
                signInCode = code
                let instructions = "Paste this code on GitHub and authorize Agent Bridge. Then return here; your machines will appear after sign-in finishes."
                signInStatus = instructions
                let signedIn = try await flow.wait(for: code, canPoll: {
                    await MainActor.run { token == self.signInGeneration && self.canPollGitHub }
                }) { connected in
                    await MainActor.run {
                        guard token == self.signInGeneration, self.isSigningIn else { return }
                        self.signInStatus = connected ? instructions : "The connection was interrupted. Agent Bridge is retrying with the same code."
                    }
                }
                try Task.checkCancellation()
                guard token == signInGeneration else { return }
                try BridgeKeychain.save(JSONEncoder().encode(signedIn), name: "account:\(address)")
                relayAddress = address
                account = signedIn
                isSigningIn = false
                signInCode = nil
                signInStatus = "Signed in as \(signedIn.login)"
                connect()
            } catch {
                guard token == signInGeneration else { return }
                isSigningIn = false
                signInCode = nil
                signInStatus = error.localizedDescription
            }
        }
    }

    func cancelSignIn() {
        signInGeneration = UUID()
        signInTask?.cancel()
        signInTask = nil
        isSigningIn = false
        signInCode = nil
        signInStatus = nil
    }

    func signOut() {
        let oldOrigin = activeOrigin
        let oldAccount = activeAccountID
        cancelSignIn()
        disconnect()
        do {
            if let activeOrigin {
                try BridgeKeychain.remove("account:\(activeOrigin)")
            }
        } catch { remoteStatus = error.localizedDescription; return }
        account = nil
        activeAccountID = nil
        roster = []
        pendingSelection = nil
        notifications.clearAll()
        cacheSession = nil
        cachedUsage = nil
        if let oldOrigin, let oldAccount {
            let clearing = usageLifecycle.clear(relay: oldOrigin, account: oldAccount)
            Task {
                do { try await clearing.value }
                catch { cacheStatus = "Could not clear saved usage: \(error.localizedDescription)" }
            }
        } else {
            usageLifecycle.deactivate()
        }
        hosts = []
        readVersions = [:]
        launchDrafts = [:]
        pinned = []
        manualOrder = []
        selectedMachine = nil
        remoteStatus = "Signed out"
        connect()
    }

    private func startRelay(_ account: RelayAccount) {
        let token = generation
        let address = relayAddress
        let name = deviceName
        relayTask = Task {
            var delay = 1
            while !Task.isCancelled, token == generation {
                do {
                    let identity: NoiseKeyPair
                    if let bytes = try BridgeKeychain.load("identity:\(address):\(account.accountId)") {
                        identity = try NoiseKeyPair(rawRepresentation: bytes)
                    } else {
                        identity = NoiseKeyPair()
                        try BridgeKeychain.save(identity.rawRepresentation, name: "identity:\(address):\(account.accountId)")
                    }
                    let kind = "app"
                    let connection = try RelayBridge(relay: address, account: account, identity: identity, name: name, kind: kind)
                    relay = connection
                    remoteStatus = "Connecting to relay…"
                    let events = try await connection.connect()
                    try Task.checkCancellation()
                    guard token == generation else { await connection.disconnect(); return }
                    delay = 1
                    remoteStatus = "Signed in as \(account.login)"
                    for try await event in events {
                        guard token == generation else { await connection.disconnect(); return }
                        switch event {
                        case let .presence(machines): applyPresence(machines, connection: connection)
                        }
                    }
                    throw RelayError.disconnected
                } catch {
                    guard token == generation else { return }
                    let old = relay
                    if let old {
                        await old.disconnect()
                    }
                    guard token == generation else { return }
                    relay = nil
                    hosts.filter { !$0.isLocal }.forEach { $0.stop() }
                    remoteStatus = "\(error.localizedDescription) Reconnecting…"
                }
                do { try await Task.sleep(for: .seconds(delay)) } catch { return }
                delay = min(delay * 2, 30)
            }
        }
    }

    private func applyPresence(_ machines: [RelayMachine], connection: RelayBridge) {
        roster = machines
        hasRosterSnapshot = true
        pruneCachedRoster()
        for host in hosts {
            if let usage = host.usage {
                cacheUsage(usage, from: host)
            }
        }
        for host in hosts where !host.isLocal && !machines.contains(where: { $0.id == host.id && $0.online }) {
            host.stop()
        }
        for machine in machines where machine.online {
            let existing = hosts.first { $0.id == machine.id }
            if let existing, existing.isRunning, hostRelays[machine.id] == ObjectIdentifier(connection) {
                continue
            }
            let provider: @Sendable () async throws -> HostTransport = {
                let channel = try await connection.open(peerID: machine.id)
                return HostTransport(messages: channel.messages, send: { try await channel.request($0, timeout: $1) },
                                     close: { await channel.disconnect() }, peerKey: channel.peerKey)
            }
            if let existing {
                existing.reconnect(provider: provider)
            } else {
                let host = HostStore(id: machine.id, name: machine.name, isLocal: false, provider: provider)
                observe(host)
                hosts.append(host)
                host.start()
            }
            hostRelays[machine.id] = ObjectIdentifier(connection)
        }
    }

    func showSamples() {
        notifications.clearAll()
        usageLifecycle.deactivate()
        cachedUsage = nil
        cacheSession = nil
        pendingSelection = nil
        disconnect(); hosts = []; isSample = true; selectedMachine = nil
    }

    func closeSamples() {
        isSample = false; connect()
    }

    func remoteHost(for host: HostStore) -> HostStore? {
        if !host.isLocal {
            return host
        }
        guard let machineID = host.hello?["machine"]?["id"]?.string else { return nil }
        return hosts.first { !$0.isLocal && $0.hello?["machine"]?["id"]?.string == machineID && $0.isLive }
    }

    func launchReceiptHost(for host: HostStore) -> HostStore {
        let candidates = launchHosts(for: host)
        if let pending = candidates.first(where: { $0.attempts["launch"]?.isSending == true || $0.attempts["launch"]?.uncertain == true }) {
            return pending
        }
        return candidates.filter { $0.attempts["launch"] != nil }.max {
            $0.attempts["launch"]!.firstSentAt < $1.attempts["launch"]!.firstSentAt
        } ?? remoteHost(for: host) ?? host
    }

    func performLaunch(_ operation: BridgeOperation, on host: HostStore) async {
        guard !launchHosts(for: host).contains(where: { $0.attempts["launch"]?.isSending == true || $0.attempts["launch"]?.uncertain == true }) else { return }
        await host.perform(operation, slot: "launch")
    }

    func reconcileTrust(questionID: String, on host: HostStore, result: JSONValue) {
        for candidate in launchHosts(for: host) {
            candidate.reconcileLaunchTrust(questionID: questionID, result: result)
        }
    }

    private func launchHosts(for host: HostStore) -> [HostStore] {
        let machineID = host.hello?["machine"]?["id"]?.string
        return hosts.filter { $0.id == host.id || machineID != nil && $0.hello?["machine"]?["id"]?.string == machineID }
    }

    func signingKey(for host: HostStore) throws -> Data {
        guard !host.isLocal, host.isLive, let key = host.peerKey,
              try RelayWire.deviceID(publicKey: key) == host.id,
              let activeOrigin, let activeAccountID, account?.accountId == activeAccountID,
              let identity = try BridgeKeychain.load("identity:\(activeOrigin):\(activeAccountID)")
        else {
            throw BridgeOperationError("Connect this machine through the signed-in relay before sending a signed command.")
        }
        return identity
    }

    func uploadBlob(_ url: URL, type: String?, to host: HostStore, onlyOn connection: UUID) async throws -> UUID {
        // Validate the composer's captured connection on this actor. Checking in
        // a separate actor hop would let reconnect replace it before upload starts.
        guard host.isLive, host.connectionID == connection else { throw BridgeOperationError("The machine connection changed. Attach the file again.") }
        guard !host.isLocal, host.can("sessions.attach.blob"), let relay, let activeOrigin else { throw RelayError.disconnected }
        let token = generation
        let (attachment, ticket, key) = try await RelayBlobUpload.prepare(file: url, name: url.lastPathComponent,
                                                                          type: type, recipient: host.id, relay: relay, origin: RelayAddress.validated(activeOrigin))
        return try await BridgeBlobStaging.confirm(attachment, ticket: ticket, key: key, capabilities: host.capabilities,
                                                   validateConnection: {
                                                       try await MainActor.run {
                                                           guard token == self.generation, host.connectionID == connection, host.isLive else { throw RelayError.disconnected }
                                                       }
                                                   }, send: { operation, timeout in
                                                       try await host.call(operation.op, data: operation.data, timeout: timeout, onlyOn: connection)
                                                   }, deleteBlob: {
                                                       try await relay.blobDelete(ticket: ticket, timeout: .seconds(5))
                                                   })
    }

    func forget(_ machineID: String) async throws {
        guard let relay, roster.contains(where: { $0.id == machineID && !$0.online }) else { throw RelayError.disconnected }
        let token = generation
        try await relay.forget(machineID: machineID)
        guard token == generation else { return }
        hosts.first { $0.id == machineID }?.stop()
        hosts.removeAll { $0.id == machineID }
        roster.removeAll { $0.id == machineID }
        hostRelays.removeValue(forKey: machineID)
        notifications.clear(machineID: machineID)
        if let cacheSession {
            let (allowed, revision) = usageMembership()
            try? await usageCache.prune(allowedMachineIDs: allowed, session: cacheSession, revision: revision)
            guard token == generation else { return }
            let report = try? await usageCache.report(session: cacheSession)
            guard token == generation, cacheSession == self.cacheSession, revision == usageRosterRevision else { return }
            cachedUsage = report
        }
    }

    private func observe(_ host: HostStore) {
        host.onMutationResult = { [weak self, weak host] operation, result in
            guard let self, let host, hosts.contains(where: { $0 === host }),
                  operation.op == "launch.trust.answer", let questionID = operation.data["question"]?.string else { return }
            reconcileTrust(questionID: questionID, on: host, result: result)
        }
        host.onDisconnected = { [weak self, weak host] in
            guard let self, let host, !isFixture, !isSample else { return }
            let machineID = host.hello?["machine"]?["id"]?.string ?? host.id
            if !hosts.contains(where: { $0.isLive && $0.hello?["machine"]?["id"]?.string == machineID }) {
                notifications.clear(machineID: machineID)
            }
        }
        host.onSessionsChanged = { [weak self, weak host] before, after, initial in
            guard let self, let host, !isFixture, !isSample else { return }
            registerSessions(after, host: host)
            resolvePendingSelection()
            let machineID = host.hello?["machine"]?["id"]?.string ?? host.id
            if !host.isLocal, hosts.contains(where: { $0.isLocal && $0.isLive && $0.hello?["machine"]?["id"]?.string == machineID }) {
                return
            }
            notifications.update(machineID: machineID, machineName: host.name, before: before, after: after, initialSnapshot: initial,
                                 capabilities: host.capabilities, scope: preferenceScope)
        }
        host.onUsageChanged = { [weak self, weak host] value in
            guard let self, let host, let value else { return }
            cacheUsage(value, from: host)
        }
    }

    private func activateUsageCache() {
        let token = generation
        guard let activeOrigin, let activeAccountID else { cacheSession = nil; usageLifecycle.deactivate(); return }
        let activation = usageLifecycle.activate(relay: activeOrigin, account: activeAccountID)
        Task {
            do {
                guard let lease = try await activation.value, token == generation else { return }
                let report = try await usageCache.report(session: lease)
                let status = await usageCache.statusMessage
                guard token == generation else { return }
                cacheSession = lease
                cachedUsage = report
                cacheStatus = status
                pruneCachedRoster()
                for host in hosts {
                    if let value = host.usage {
                        cacheUsage(value, from: host)
                    }
                }
            } catch {
                if token == generation {
                    cacheStatus = error.localizedDescription
                }
            }
        }
    }

    private func cacheUsage(_ value: JSONValue, from host: HostStore) {
        guard let lease = cacheSession, hasRosterSnapshot, !isFixture, !isSample else { return }
        let token = generation
        let machineID = host.hello?["machine"]?["id"]?.string ?? host.id
        Task {
            do {
                guard token == generation, lease == cacheSession else { return }
                let (allowed, revision) = usageMembership()
                let report = try await usageCache.merge(report: BridgeUsageReport(json: value), machineID: machineID,
                                                        machineName: host.name, allowedMachineIDs: allowed, session: lease, revision: revision)
                guard token == generation, lease == cacheSession, revision == usageRosterRevision else { return }
                cachedUsage = report
            } catch {
                if token == generation {
                    cacheStatus = error.localizedDescription
                }
            }
        }
    }

    private var allowedUsageMachines: Set<String> {
        Set(roster.map(\.id) + hosts.filter(\.isLocal).compactMap { $0.hello?["machine"]?["id"]?.string })
    }

    private func usageMembership() -> (Set<String>, Int) {
        let allowed = allowedUsageMachines
        if allowed != usageRoster {
            usageRosterRevision += 1; usageRoster = allowed
        }
        return (allowed, usageRosterRevision)
    }

    private func pruneCachedRoster() {
        guard hasRosterSnapshot, let lease = cacheSession else { return }
        let token = generation
        let (allowed, revision) = usageMembership()
        Task {
            do {
                guard token == generation, lease == cacheSession else { return }
                try await usageCache.prune(allowedMachineIDs: allowed, session: lease, revision: revision)
                let report = try await usageCache.report(session: lease)
                guard token == generation, lease == cacheSession, revision == usageRosterRevision else { return }
                cachedUsage = report
            } catch {
                if token == generation {
                    cacheStatus = error.localizedDescription
                }
            }
        }
    }

    func togglePin(_ id: String) {
        if pinned.contains(id) {
            pinned.remove(id)
        } else {
            pinned.insert(id)
        }
        UserDefaults.standard.set(Array(pinned), forKey: preferenceScope + ".pins")
    }

    func markRead(_ entry: SessionEntry) {
        readVersions[entry.id] = entry.session.updatedAt ?? ""
        UserDefaults.standard.set(readVersions, forKey: preferenceScope + ".read")
    }

    func isUnread(_ entry: SessionEntry) -> Bool {
        !entry.ended && readVersions[entry.id] != nil && readVersions[entry.id] != entry.session.updatedAt && entry.session.updatedAt != nil
    }

    private func resolvePendingSelection() {
        guard let pendingSelection,
              let entry = entries.first(where: { $0.session.id == pendingSelection.session && ($0.host?.hello?["machine"]?["id"]?.string ?? $0.host?.id) == pendingSelection.machine }) else { return }
        selectedSessionID = entry.id
        self.pendingSelection = nil
    }

    func openSession(_ sessionID: String, on host: HostStore) {
        pendingSelection = (host.hello?["machine"]?["id"]?.string ?? host.id, sessionID)
        resolvePendingSelection()
        onOpenSession?()
    }

    private func registerSessions(_ sessions: [BridgeSession], host: HostStore) {
        let machineID = host.hello?["machine"]?["id"]?.string ?? host.id
        let orderMembership = Set(manualOrder)
        let newSessions = sessions.filter { !orderMembership.contains(machineID + ":" + $0.id) }.sorted { ($0.updatedAt ?? "") > ($1.updatedAt ?? "") }
        guard !newSessions.isEmpty else { return }
        let newIDs = newSessions.map { machineID + ":" + $0.id }
        manualOrder = Array((newIDs + manualOrder).prefix(300))
        for session in newSessions {
            let id = machineID + ":" + session.id
            if readVersions[id] == nil {
                readVersions[id] = session.updatedAt ?? ""
            }
        }
        let kept = Set(manualOrder)
        readVersions = readVersions.filter { kept.contains($0.key) }
        pinned.formIntersection(kept)
        UserDefaults.standard.set(manualOrder, forKey: preferenceScope + ".order")
        UserDefaults.standard.set(readVersions, forKey: preferenceScope + ".read")
        UserDefaults.standard.set(Array(pinned), forKey: preferenceScope + ".pins")
    }

    func move(_ id: String, by distance: Int) {
        var order = sorted(entries).map(\.id)
        guard let index = order.firstIndex(of: id) else { return }
        let target = min(max(0, index + distance), order.count - 1)
        order.remove(at: index)
        order.insert(id, at: target)
        manualOrder = order
        sortOrder = "manual"
        UserDefaults.standard.set(order, forKey: preferenceScope + ".order")
    }

    func sorted(_ entries: [SessionEntry]) -> [SessionEntry] {
        SessionOrdering.sorted(entries, pinned: pinned, manualOrder: manualOrder, sortOrder: sortOrder)
    }

    private func loadPreferences() {
        pinned = Set(UserDefaults.standard.stringArray(forKey: preferenceScope + ".pins") ?? [])
        manualOrder = UserDefaults.standard.stringArray(forKey: preferenceScope + ".order") ?? []
        readVersions = UserDefaults.standard.dictionary(forKey: preferenceScope + ".read") as? [String: String] ?? [:]
    }
}
