import Foundation

// Keep in step with src/AgentBridge.Protocol/Invisible.cs: agents can discard these
// scalars before acting on the first visible base character.
enum Invisible {
    static func blank(_ text: String) -> Bool {
        first(text) == nil
    }

    static func first(_ text: String) -> Unicode.Scalar? {
        text.unicodeScalars.first { !isInvisible($0) }
    }

    private static func isInvisible(_ scalar: Unicode.Scalar) -> Bool {
        let properties = scalar.properties
        if properties.isWhitespace || scalar.value == 0x2800 || properties.isDefaultIgnorableCodePoint {
            return true
        }
        switch properties.generalCategory {
        case .control, .format, .unassigned, .nonspacingMark, .spacingMark, .enclosingMark:
            return true
        default:
            return false
        }
    }
}
