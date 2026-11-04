@testable import AgentBridgeAppleCore
import Foundation
import Testing

private final class SignInClock: @unchecked Sendable {
    private let lock = NSLock()
    private var instant = ContinuousClock.now
    private var delays: [Duration] = []
    let holdSleep: Bool
    let sleeping = AsyncStream<Duration>.makeStream()

    init(holdSleep: Bool = false) {
        self.holdSleep = holdSleep
    }

    var now: ContinuousClock.Instant {
        lock.withLock { instant }
    }

    var sleeps: [Duration] {
        lock.withLock { delays }
    }

    func advance(_ duration: Duration) {
        lock.withLock { instant = instant.advanced(by: duration) }
    }

    func sleep(_ duration: Duration) async throws {
        lock.withLock { delays.append(duration) }
        sleeping.continuation.yield(duration)
        if holdSleep {
            try await Task.sleep(for: .seconds(3600))
        }
        try Task.checkCancellation()
        advance(duration)
    }
}

private struct SignInReply: Sendable {
    let status: Int
    let body: Data
    let location: URL?
    var error: URLError.Code?
    init(_ status: Int, _ body: String, location: URL? = nil) {
        self.status = status; self.body = Data(body.utf8); self.location = location
    }

    init(_ error: URLError.Code) {
        self.init(0, "")
        self.error = error
    }
}

private actor ConnectivityChanges {
    private(set) var values: [Bool] = []
    func record(_ connected: Bool) {
        values.append(connected)
    }
}

private actor PollPermissions {
    var values: [Bool]
    init(_ values: [Bool]) {
        self.values = values
    }

    func next() -> Bool {
        values.isEmpty ? false : values.removeFirst()
    }
}

/// Every fixture has a unique HTTPS origin and a private URLSession. The protocol
/// never opens sockets, including when a response tries to redirect the session.
private final class SignInHTTP: @unchecked Sendable {
    private let lock = NSLock()
    private var replies: [SignInReply]
    private var received: [URLRequest] = []
    private var receivedBodies: [Data] = []
    private let onRequest: @Sendable () -> Void
    let origin = URL(string: "https://auth-\(UUID().uuidString.lowercased()).example")!
    var requests: [URLRequest] {
        lock.withLock { received }
    }

    var bodies: [Data] {
        lock.withLock { receivedBodies }
    }

    init(_ replies: [SignInReply], onRequest: @escaping @Sendable () -> Void = {}) {
        self.replies = replies; self.onRequest = onRequest
    }

    func next(_ request: URLRequest) -> SignInReply {
        onRequest()
        var body = request.httpBody ?? Data()
        if body.isEmpty, let stream = request.httpBodyStream {
            stream.open()
            defer { stream.close() }
            var buffer = [UInt8](repeating: 0, count: 1024)
            while body.count < 16384 {
                let count = stream.read(&buffer, maxLength: buffer.count)
                guard count > 0 else { break }
                body.append(contentsOf: buffer.prefix(count))
            }
        }
        return lock.withLock {
            received.append(request)
            receivedBodies.append(body)
            return replies.isEmpty ? SignInReply(500, "unexpected fixture request") : replies.removeFirst()
        }
    }

    func makeSession() -> URLSession {
        SignInURLProtocol.fixtures.add(self)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [SignInURLProtocol.self]
        configuration.timeoutIntervalForRequest = 1
        configuration.timeoutIntervalForResource = 2
        return URLSession(configuration: configuration, delegate: NoRedirects(), delegateQueue: nil)
    }

    func client(_ clock: SignInClock) throws -> (RelaySignIn, URLSession) {
        let session = makeSession()
        return try (RelaySignIn(relay: origin.absoluteString, session: session, now: { clock.now }, sleep: { try await clock.sleep($0) }), session)
    }
}

private final class SignInFixtures: @unchecked Sendable {
    private let lock = NSLock()
    private var values: [String: SignInHTTP] = [:]
    func add(_ fixture: SignInHTTP) {
        lock.withLock { values[fixture.origin.host!] = fixture }
    }

    func get(_ request: URLRequest) -> SignInHTTP? {
        lock.withLock { values[request.url?.host ?? ""] }
    }

    func remove(_ fixture: SignInHTTP) {
        _ = lock.withLock { values.removeValue(forKey: fixture.origin.host!) }
    }
}

private final class SignInURLProtocol: URLProtocol, @unchecked Sendable {
    static let fixtures = SignInFixtures()
    override class func canInit(with _: URLRequest) -> Bool {
        true
    }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest {
        request
    }

    override func startLoading() {
        guard let fixture = Self.fixtures.get(request), let url = request.url else {
            client?.urlProtocol(self, didFailWithError: URLError(.unsupportedURL)); return
        }
        let reply = fixture.next(request)
        if let error = reply.error {
            client?.urlProtocol(self, didFailWithError: URLError(error))
            return
        }
        let headers = reply.location.map { ["Location": $0.absoluteString] }
        let response = HTTPURLResponse(url: url, statusCode: reply.status, httpVersion: "HTTP/1.1", headerFields: headers)!
        if let location = reply.location {
            client?.urlProtocol(self, wasRedirectedTo: URLRequest(url: location), redirectResponse: response)
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: reply.body)
            client?.urlProtocolDidFinishLoading(self)
        } else {
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: reply.body)
            client?.urlProtocolDidFinishLoading(self)
        }
    }

    override func stopLoading() {}
}

private let validDeviceCode = #"{"deviceCode":"fixture-device-code","userCode":"ABCD-EFGH","verificationUri":"https://github.com/login/device","interval":2,"expiresIn":30}"#
private let validAccount = #"{"token":"fixture-token","accountId":"github:fixture","login":"fixture"}"#

private func pollingCode(interval: Int = 2, expiresIn: Int = 30) throws -> RelayDeviceCode {
    try JSONDecoder().decode(RelayDeviceCode.self, from: JSONEncoder().encode(JSONValue.object([
        "deviceCode": .string("fixture-device-code"), "userCode": .string("ABCD-EFGH"),
        "verificationUri": .string("https://github.com/login/device"), "interval": .number(Double(interval)), "expiresIn": .number(Double(expiresIn)),
    ])))
}

@Test func signInPollsTheSameDeviceCodeAndHonorsPendingIntervalChanges() async throws {
    let clock = SignInClock()
    let http = SignInHTTP([.init(200, validDeviceCode), .init(202, #"{"interval":7}"#), .init(202, #"{"interval":0}"#), .init(200, validAccount)])
    let (client, session) = try http.client(clock)
    defer { session.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(http) }
    let code = try await client.start()
    #expect(try await client.wait(for: code) == RelayAccount(token: "fixture-token", accountId: "github:fixture", login: "fixture"))
    #expect(clock.sleeps == [.seconds(2), .seconds(7), .seconds(7)])
    #expect(http.requests.map { $0.url?.path } == ["/v1/auth/device", "/v1/auth/token", "/v1/auth/token", "/v1/auth/token"])
    #expect(http.requests.allSatisfy { $0.httpMethod == "POST" && $0.url?.host == http.origin.host && $0.value(forHTTPHeaderField: "Content-Type") == "application/json" })
    for body in http.bodies.dropFirst() {
        #expect(try JSONDecoder().decode(JSONValue.self, from: body) == .object(["deviceCode": .string("fixture-device-code")]))
    }
}

@Test func cancellingSignInDuringThePollDelaySendsNoTokenRequest() async throws {
    let clock = SignInClock(holdSleep: true)
    let http = SignInHTTP([])
    let (client, session) = try http.client(clock)
    defer { session.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(http) }
    let code = try pollingCode()
    let task = Task { try await client.wait(for: code) }
    var sleeps = clock.sleeping.stream.makeAsyncIterator()
    _ = await sleeps.next()
    task.cancel()
    await #expect(throws: CancellationError.self) { try await task.value }
    #expect(http.requests.isEmpty)
}

@Test func signInKeepsTheApprovedCodeAcrossTransientNetworkLoss() async throws {
    let clock = SignInClock()
    let http = SignInHTTP([.init(200, validDeviceCode), .init(.networkConnectionLost), .init(.timedOut), .init(202, #"{"interval":2}"#), .init(200, validAccount)])
    let (client, session) = try http.client(clock)
    defer { session.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(http) }
    let changes = ConnectivityChanges()
    let code = try await client.start()
    let account = try await client.wait(for: code, onConnectivityChange: { await changes.record($0) })
    #expect(account.login == "fixture")
    #expect(await changes.values == [false, true])
    #expect(clock.sleeps == [.seconds(2), .seconds(4), .seconds(8), .seconds(8)])
    #expect(http.requests.map { $0.url?.path } == ["/v1/auth/device", "/v1/auth/token", "/v1/auth/token", "/v1/auth/token", "/v1/auth/token"])
    for body in http.bodies.dropFirst() {
        #expect(try JSONDecoder().decode(JSONValue.self, from: body) == .object(["deviceCode": .string(code.deviceCode)]))
    }
}

@Test func anOfflinePollCannotExtendTheOriginalDeviceCodeDeadline() async throws {
    let clock = SignInClock()
    let http = SignInHTTP([.init(.notConnectedToInternet)])
    let (client, session) = try http.client(clock)
    defer { session.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(http) }
    await #expect(throws: RelayError.refused("The code expired. Start sign-in again.")) {
        try await client.wait(for: pollingCode(interval: 2, expiresIn: 3))
    }
    #expect(http.requests.count == 1)
    #expect(clock.sleeps == [.seconds(2), .seconds(1)])
}

@Test func repeatedNetworkInterruptionsCapBackoffAtOneMinute() async throws {
    let clock = SignInClock()
    let http = SignInHTTP([.init(.networkConnectionLost), .init(.timedOut), .init(200, validAccount)])
    let (client, session) = try http.client(clock)
    defer { session.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(http) }
    #expect(try await client.wait(for: pollingCode(interval: 40, expiresIn: 240)).login == "fixture")
    #expect(clock.sleeps == [.seconds(40), .seconds(60), .seconds(60)])
}

@Test func backgroundPollsWaitUntilTheAppReturnsBeforeExchangingTheSameCode() async throws {
    let clock = SignInClock()
    let http = SignInHTTP([.init(200, validAccount)])
    let (client, session) = try http.client(clock)
    defer { session.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(http) }
    let visibility = PollPermissions([false, false, true])
    let code = try pollingCode()
    #expect(try await client.wait(for: code, canPoll: { await visibility.next() }).login == "fixture")
    #expect(clock.sleeps == [.seconds(2), .seconds(2), .seconds(2)])
    #expect(http.requests.count == 1)
    #expect(try JSONDecoder().decode(JSONValue.self, from: #require(http.bodies.first)) == .object(["deviceCode": .string(code.deviceCode)]))
}

@Test func backgroundSuspensionCannotExtendTheOriginalCodeLifetime() async throws {
    let clock = SignInClock()
    let http = SignInHTTP([])
    let (client, session) = try http.client(clock)
    defer { session.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(http) }
    await #expect(throws: RelayError.refused("The code expired. Start sign-in again.")) {
        try await client.wait(for: pollingCode(interval: 2, expiresIn: 5), canPoll: { false })
    }
    #expect(clock.sleeps == [.seconds(2), .seconds(2), .seconds(1)])
    #expect(http.requests.isEmpty)
}

@Test(arguments: [URLError.Code.serverCertificateUntrusted, .cancelled])
func certificateFailureAndCancellationNeverRetryTheTokenPoll(_ error: URLError.Code) async throws {
    let http = SignInHTTP([.init(error), .init(200, validAccount)])
    let (client, session) = try http.client(SignInClock())
    defer { session.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(http) }
    do {
        _ = try await client.wait(for: pollingCode())
        Issue.record("an untrusted or canceled request must not complete sign-in")
    } catch let failure as URLError {
        #expect(failure.code == error)
    }
    #expect(http.requests.count == 1)
}

@Test func signInExpiresWithoutPollingAtOrBeyondTheDeadline() async throws {
    let clock = SignInClock()
    let http = SignInHTTP([.init(202, "{}")])
    let (client, session) = try http.client(clock)
    defer { session.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(http) }
    await #expect(throws: RelayError.refused("The code expired. Start sign-in again.")) {
        try await client.wait(for: pollingCode(interval: 2, expiresIn: 3))
    }
    #expect(http.requests.count == 1)
    #expect(clock.sleeps == [.seconds(2), .seconds(1)])
}

@Test func aSuccessfulTokenResponseArrivingAfterExpiryDoesNotSignIn() async throws {
    let clock = SignInClock()
    let http = SignInHTTP([.init(200, validAccount)], onRequest: { clock.advance(.seconds(30)) })
    let (client, session) = try http.client(clock)
    defer { session.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(http) }
    await #expect(throws: RelayError.refused("The code expired. Start sign-in again.")) {
        try await client.wait(for: pollingCode())
    }
    #expect(http.requests.count == 1)
}

@Test func signInRejectsRedirectsWithoutRequestingTheirDestination() async throws {
    let clock = SignInClock()
    let destination = SignInHTTP([.init(200, validDeviceCode)])
    SignInURLProtocol.fixtures.add(destination)
    let http = SignInHTTP([.init(302, "", location: destination.origin.appendingPathComponent("collect"))])
    let (client, session) = try http.client(clock)
    defer { session.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(http); SignInURLProtocol.fixtures.remove(destination) }
    await #expect(throws: RelayError.refused("Sign-in is unavailable (302).")) { try await client.start() }
    #expect(http.requests.count == 1)
    #expect(destination.requests.isEmpty)
}

@Test(arguments: ["http://github.com/login/device", "https://github.com.evil.example/login/device", "https://user@github.com/login/device", "https://user:password@github.com/login/device"])
func signInRejectsVerificationURLsOutsideUncredentialedGitHubHTTPS(_ url: String) async throws {
    let json = validDeviceCode.replacingOccurrences(of: "https://github.com/login/device", with: url)
    let http = SignInHTTP([.init(200, json)])
    let (client, session) = try http.client(SignInClock())
    defer { session.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(http) }
    await #expect(throws: RelayError.invalidMessage) { try await client.start() }
}

@Test func signInBoundsResponseBodiesAndDoesNotReflectRefusalSecrets() async throws {
    let maximumBody = validAccount + String(repeating: " ", count: 16384 - validAccount.utf8.count)
    let accepted = SignInHTTP([.init(200, maximumBody)])
    let (validClient, validSession) = try accepted.client(SignInClock())
    defer { validSession.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(accepted) }
    #expect(try await validClient.wait(for: pollingCode()).accountId == "github:fixture")
    for reply in [SignInReply(200, maximumBody + " "), SignInReply(403, "fixture-secret-must-not-be-displayed")] {
        let http = SignInHTTP([reply])
        let (client, session) = try http.client(SignInClock())
        defer { session.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(http) }
        do {
            _ = try await client.wait(for: pollingCode())
            Issue.record("Expected invalid or refused authentication response")
        } catch {
            #expect(!error.localizedDescription.contains("fixture-secret"))
            if reply.status == 200 {
                #expect(error as? RelayError == .invalidMessage)
            } else {
                #expect(error as? RelayError == .refused("Sign-in was refused or the code expired. Start sign-in again."))
            }
        }
    }
}

@Test func relayAddressesNormalizeOnlyAnUncredentialedHTTPSOrigin() throws {
    #expect(try RelayAddress.validated(" HTTPS://Relay.Example:443/ \n").absoluteString == "https://relay.example")
    #expect(try RelayAddress.validated("https://relay.example:8443").absoluteString == "https://relay.example:8443")
    for address in ["http://relay.example", "https://user@relay.example", "https://user:pass@relay.example", "https://relay.example/path", "https://relay.example?token=x", "https://relay.example#fragment", "https://", "file:///tmp/relay"] {
        #expect(throws: RelayError.invalidURL) { try RelayAddress.validated(address) }
    }
}

@Test func signInAcceptsOnlyTheExactDevicePageFromTheRelay() async throws {
    struct Row: Decodable { let uri: String; let valid: Bool }
    let file = try #require(Bundle.module.url(forResource: "device-pages", withExtension: "json"))
    for row in try JSONDecoder().decode([Row].self, from: Data(contentsOf: file)) {
        var payload = try #require(JSONSerialization.jsonObject(with: Data(validDeviceCode.utf8)) as? [String: Any])
        payload["verificationUri"] = row.uri
        let body = try JSONSerialization.data(withJSONObject: payload)
        let http = SignInHTTP([.init(200, String(decoding: body, as: UTF8.self))])
        let (client, session) = try http.client(SignInClock())
        defer { session.invalidateAndCancel(); SignInURLProtocol.fixtures.remove(http) }
        if row.valid {
            #expect(try await client.start().verificationUri.absoluteString == row.uri)
        } else {
            await #expect(throws: RelayError.invalidMessage) { try await client.start() }
        }
    }
}
