import Foundation

public struct RelayAccount: Codable, Sendable, Equatable {
    public let token: String
    public let accountId: String
    public let login: String

    public init(token: String, accountId: String, login: String) {
        self.token = token
        self.accountId = accountId
        self.login = login
    }
}

public struct RelayDeviceCode: Decodable, Sendable {
    public let deviceCode: String
    public let userCode: String
    public let verificationUri: URL
    public let interval: Int
    public let expiresIn: Int
}

public enum RelayError: LocalizedError, Sendable, Equatable {
    case invalidURL, invalidMessage, disconnected, timeout, overflow, refused(String)

    public var errorDescription: String? {
        switch self {
        case .invalidURL: "Enter an HTTPS relay address without credentials, a query, or a fragment."
        case .invalidMessage: "The relay sent an invalid message."
        case .disconnected: "The relay connection ended."
        case .timeout: "The connection did not respond in time."
        case .overflow: "Too many updates arrived. Reconnect to synchronize again."
        case let .refused(reason): reason
        }
    }
}

/// Origin-only URLs prevent an account token from being sent to a different origin
/// through relative paths or redirects. Production connections always use TLS.
public enum RelayAddress {
    public static func validated(_ value: String) throws -> URL {
        guard var parts = URLComponents(string: value.trimmingCharacters(in: .whitespacesAndNewlines)),
              parts.scheme?.lowercased() == "https", let host = parts.host, !host.isEmpty,
              parts.user == nil, parts.password == nil, parts.query == nil, parts.fragment == nil,
              parts.path.isEmpty || parts.path == "/", parts.url != nil
        else { throw RelayError.invalidURL }
        parts.scheme = "https"
        parts.host = host.lowercased()
        parts.path = ""
        if parts.port == 443 {
            parts.port = nil
        }
        guard let url = parts.url else { throw RelayError.invalidURL }
        return url
    }
}

final class NoRedirects: NSObject, URLSessionTaskDelegate, Sendable {
    func urlSession(_: URLSession, task _: URLSessionTask, willPerformHTTPRedirection _: HTTPURLResponse,
                    newRequest _: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void)
    {
        completionHandler(nil)
    }
}

public struct RelaySignIn: Sendable {
    private let base: URL
    private let session: URLSession

    public init(relay: String) throws {
        base = try RelayAddress.validated(relay)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 20
        session = URLSession(configuration: configuration, delegate: NoRedirects(), delegateQueue: nil)
    }

    public func start() async throws -> RelayDeviceCode {
        let (data, response) = try await post("v1/auth/device", body: nil)
        guard response.statusCode == 200 else { throw RelayError.refused("Sign-in is unavailable (\(response.statusCode)).") }
        let code = try JSONDecoder().decode(RelayDeviceCode.self, from: data)
        guard !code.deviceCode.isEmpty, !code.userCode.isEmpty, code.expiresIn > 0,
              code.expiresIn <= 3600, code.interval > 0, code.interval <= 60,
              code.verificationUri.scheme == "https", code.verificationUri.host == "github.com",
              code.verificationUri.user == nil, code.verificationUri.password == nil
        else { throw RelayError.invalidMessage }
        return code
    }

    public func wait(for code: RelayDeviceCode) async throws -> RelayAccount {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(code.expiresIn))
        var interval = code.interval
        while clock.now < deadline {
            try await Task.sleep(for: .seconds(interval))
            try Task.checkCancellation()
            guard clock.now < deadline else { break }
            let body = try JSONEncoder().encode(["deviceCode": code.deviceCode])
            let (data, response) = try await post("v1/auth/token", body: body)
            if response.statusCode == 202 {
                if let json = try? JSONDecoder().decode(JSONValue.self, from: data),
                   let next = json["interval"]?.number, next.isFinite, next >= 1, next <= 60
                {
                    interval = Int(next)
                }
                continue
            }
            guard response.statusCode == 200 else {
                // Do not reflect arbitrary server error text that might contain a token.
                throw RelayError.refused("Sign-in was refused or the code expired. Start sign-in again.")
            }
            let account = try JSONDecoder().decode(RelayAccount.self, from: data)
            guard !account.token.isEmpty, !account.accountId.isEmpty, !account.login.isEmpty else { throw RelayError.invalidMessage }
            return account
        }
        throw RelayError.refused("The code expired. Start sign-in again.")
    }

    private func post(_ path: String, body: Data?) async throws -> (Data, HTTPURLResponse) {
        var request = URLRequest(url: base.appendingPathComponent(path))
        request.httpMethod = "POST"
        request.httpBody = body
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let (bytes, response) = try await session.bytes(for: request)
        guard let response = response as? HTTPURLResponse else { bytes.task.cancel(); throw RelayError.invalidMessage }
        var data = Data()
        for try await byte in bytes {
            guard data.count < 16384 else { bytes.task.cancel(); throw RelayError.invalidMessage }
            data.append(byte)
        }
        return (data, response)
    }
}
