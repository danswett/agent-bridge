import Foundation

/// Delegate names are untrusted task descriptions. Every attribution uses `titled`
/// so an unrecognized look-alike can never replace the literal Delegate role.
public enum BridgeDelegateLabel {
    public static let maximumLength = 60

    public static func label(name: String?, id: String) -> String {
        let name = clean(name, maximum: maximumLength)
        guard !name.isEmpty else {
            let shortID = clean(id, maximum: 128).unicodeScalars.filter {
                asciiLetterOrDigit($0.value) || $0 == "-" || $0 == "_"
            }.prefix(8)
            let suffix = String(String.UnicodeScalarView(shortID))
            return suffix.isEmpty ? "Delegate" : "Delegate \(suffix)"
        }
        guard posesAsMainAgent(name) else { return name }
        let unquoted = name.unicodeScalars.map { scalar in
            "\"“”„‟«»＂".unicodeScalars.contains(scalar) ? "'" : String(scalar)
        }.joined()
        return "Delegate “\(unquoted)”"
    }

    public static func titled(_ label: String) -> String {
        label == "Delegate" || label.hasPrefix("Delegate ") ? label : "Delegate \(label)"
    }

    /// Only a literal ASCII identifier may be inserted into a draft stop request.
    public static func safeID(_ id: String) -> String? {
        guard !id.isEmpty, id.utf8.count <= 128,
              id.unicodeScalars.allSatisfy({ asciiLetterOrDigit($0.value) || "-_.:".unicodeScalars.contains($0) }) else { return nil }
        return id
    }

    private static func clean(_ text: String?, maximum: Int) -> String {
        guard let text, !text.isEmpty else { return "" }
        var result = ""
        var space = false
        for scalar in text.unicodeScalars {
            if scalar.properties.isWhitespace {
                space = !result.isEmpty
                continue
            }
            guard !hidden(scalar) else { continue }
            if space {
                result.append(" ")
                space = false
            }
            result.unicodeScalars.append(scalar)
        }
        guard result.count > maximum else { return result }
        return String(result.prefix(maximum - 1)).trimmingCharacters(in: .whitespaces) + "…"
    }

    private static func hidden(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.properties.generalCategory {
        case .format, .control, .surrogate, .privateUse, .unassigned:
            return true
        default:
            break
        }
        switch scalar.value {
        case 0x034F, 0x115F, 0x1160, 0x17B4, 0x17B5, 0x3164, 0xFFA0,
             0x180B ... 0x180F, 0xFE00 ... 0xFE0F, 0xE0000 ... 0xE0FFF:
            return true
        default:
            return false
        }
    }

    private static func posesAsMainAgent(_ name: String) -> Bool {
        var folded = ""
        for scalar in name.decomposedStringWithCompatibilityMapping.unicodeScalars {
            switch scalar.properties.generalCategory {
            case .nonspacingMark, .spacingMark, .enclosingMark:
                continue
            case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .modifierLetter, .otherLetter, .decimalNumber:
                for lower in String(scalar).lowercased().unicodeScalars {
                    folded += lookAlike(lower) ?? String(lower)
                }
            default:
                folded.append(" ")
            }
        }
        let words = folded.split(separator: " ")
        for index in words.indices {
            if words[index].hasPrefix("mainagent") {
                return true
            }
            if words[index] == "main", index + 1 < words.count, words[index + 1].hasPrefix("agent") {
                return true
            }
        }
        return false
    }

    private static func lookAlike(_ scalar: Unicode.Scalar) -> String? {
        switch scalar.value {
        case 0x043C, 0x03BC: "m"
        case 0x0430, 0x03B1, 0x0251: "a"
        case 0x0456, 0x04CF, 0x03B9, 0x0131, 0x0269, 0x04C0: "i"
        case 0x03BD, 0x0578, 0x057C: "n"
        case 0x0261, 0x050D, 0x0581: "g"
        case 0x0435, 0x03B5: "e"
        case 0x0442, 0x03C4: "t"
        default: nil
        }
    }

    private static func asciiLetterOrDigit(_ value: UInt32) -> Bool {
        (48 ... 57).contains(value) || (65 ... 90).contains(value) || (97 ... 122).contains(value)
    }
}
