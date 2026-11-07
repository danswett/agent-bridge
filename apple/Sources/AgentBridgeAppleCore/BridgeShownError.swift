import Foundation

/// An error whose every case is fixed text written in this app, never text from outside it.
public protocol BridgeFixedSentenceError: Error {
    var sentence: String { get }
}

/// The only way an error becomes words on screen (ui.md §6.13, #826). Text from the
/// machine, the relay or the system is never shown, verbatim or trimmed; it can carry a
/// secret. A case with no host sentence reads as one fixed, neutral sentence.
public enum BridgeShownError {
    public static let refused = "The host refused the request."
    public static let unknown = "Something went wrong. Try again."
    public static let signInFailed = "Sign-in failed."
    public static let signInRefused = "Sign-in refused."

    /// Every case maps to a fixed sentence written in this app. Nothing a host, the relay or
    /// the system sent is passed through: `BridgeOperationError` and `RelayRefusal` accept
    /// literals only, and `RelayError.hostRefused` drops the host's reason.
    public static func sentence(for error: any Error) -> String {
        switch error {
        case let error as BridgeOperationError: error.message
        case let error as RelayError: error.errorDescription ?? unknown
        case let error as any BridgeFixedSentenceError: error.sentence
        default: unknown
        }
    }

    /// Sign-in has its own words: a host refusal carries no reason, and `signin.done` carries none.
    public static func signInSentence(for error: any Error) -> String {
        switch error {
        case RelayError.hostRefused: signInRefused
        case let error as RelayError: error.errorDescription ?? signInFailed
        default: signInFailed
        }
    }
}
