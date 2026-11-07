import Foundation

/// The only way an error becomes words on screen (ui.md §6.13, #826). Text from the
/// machine, the relay or the system is never shown, verbatim or trimmed; it can carry a
/// secret. A case with no host sentence reads as one fixed, neutral sentence.
public enum BridgeShownError {
    public static let refused = "The host refused the request."
    public static let unknown = "Something went wrong. Try again."
    public static let signInFailed = "Sign-in failed."
    public static let signInRefused = "Sign-in refused."

    public static func sentence(for error: any Error) -> String {
        switch error {
        case let error as BridgeOperationError: error.message
        case RelayError.refused: refused
        case let error as RelayError: error.errorDescription ?? unknown
        default: unknown
        }
    }

    /// Sign-in has its own words: a refusal carries no reason unless the host sent a
    /// categorised one, and `signin.done` carries none.
    public static func signInSentence(for error: any Error) -> String {
        switch error {
        case RelayError.refused: signInRefused
        case let error as RelayError: error.errorDescription ?? signInFailed
        default: signInFailed
        }
    }
}
