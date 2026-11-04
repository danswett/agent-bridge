import Foundation

/// A bounded native-text document. It contains no HTML, image loader, or executable content.
public struct BridgeMarkdownDocument: Sendable, Equatable {
    public struct Block: Identifiable, Sendable, Equatable {
        public let id: Int
        public let kind: Kind
    }

    public indirect enum Kind: Sendable, Equatable {
        case paragraph(String), heading(Int, String), code(String), quote([Block])
        case list(ordered: Bool, start: Int, items: [Item])
        case rule, table(headers: [String], rows: [[String]])
    }

    public struct Item: Identifiable, Sendable, Equatable {
        public let id: Int
        public let blocks: [Block]
    }

    public let blocks: [Block]
    public let isTruncated: Bool
    public static let maximumBytes = 256 * 1024
    public static let maximumLines = 4096

    public init(_ source: String) {
        let bounded = String(decoding: source.utf8.prefix(Self.maximumBytes), as: UTF8.self)
        let lines = bounded.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n").components(separatedBy: "\n")
        var shortened = source.utf8.count > Self.maximumBytes || lines.count > Self.maximumLines
        blocks = Self.parse(Array(lines.prefix(Self.maximumLines)).enumerated().map { Line(id: $0.offset, text: $0.element) }, depth: 0, shortened: &shortened)
        isTruncated = shortened
    }

    private struct Line { let id: Int; let text: String }
    private struct Marker { let ordered: Bool; let start: Int; let indent: Int; let contentColumn: Int; let text: String }
    private static func trim(_ text: String) -> String {
        text.trimmingCharacters(in: .whitespaces)
    }

    private static func indent(_ text: String) -> Int {
        text.prefix { $0 == " " }.count
    }

    private static func parse(_ lines: [Line], depth: Int, shortened: inout Bool) -> [Block] {
        guard depth < 6 else { return lines.isEmpty ? [] : [Block(id: lines[0].id, kind: .paragraph(lines.map(\.text).joined(separator: "\n")))] }
        var result: [Block] = []
        var index = 0
        while index < lines.count {
            let line = lines[index]
            let text = trim(line.text)
            if text.isEmpty {
                index += 1; continue
            }
            if indentedCode(line.text) != nil {
                var code: [String] = []
                while index < lines.count {
                    if let content = indentedCode(lines[index].text) {
                        code.append(content); index += 1
                    } else if trim(lines[index].text).isEmpty {
                        code.append(""); index += 1
                    } else {
                        break
                    }
                }
                while code.last == "" {
                    code.removeLast()
                }
                result.append(Block(id: line.id, kind: .code(code.joined(separator: "\n"))))
            } else if let fence = fence(text) {
                index += 1
                var code: [String] = []
                while index < lines.count {
                    let closing = trim(lines[index].text)
                    if closing.count >= fence.count, closing.allSatisfy({ $0 == fence.character }) {
                        index += 1; break
                    }
                    code.append(lines[index].text); index += 1
                }
                result.append(Block(id: line.id, kind: .code(code.joined(separator: "\n"))))
            } else if rule(text) {
                result.append(Block(id: line.id, kind: .rule)); index += 1
            } else if let heading = heading(text) {
                result.append(Block(id: line.id, kind: .heading(heading.0, heading.1))); index += 1
            } else if text.hasPrefix(">") {
                var quote: [Line] = []
                while index < lines.count, trim(lines[index].text).hasPrefix(">") {
                    let quoted = String(trim(lines[index].text).dropFirst())
                    quote.append(Line(id: lines[index].id, text: quoted.hasPrefix(" ") ? String(quoted.dropFirst()) : quoted)); index += 1
                }
                result.append(Block(id: line.id, kind: .quote(parse(quote, depth: depth + 1, shortened: &shortened))))
            } else if let first = marker(line.text) {
                var items: [Item] = []
                while index < lines.count, let current = marker(lines[index].text), current.indent == first.indent, current.ordered == first.ordered {
                    let itemID = lines[index].id
                    var content = [Line(id: itemID, text: current.text)]
                    index += 1
                    while index < lines.count {
                        let next = lines[index]
                        if trim(next.text).isEmpty {
                            if index + 1 < lines.count, indent(lines[index + 1].text) > first.indent {
                                content.append(Line(id: next.id, text: "")); index += 1; continue
                            }
                            break
                        }
                        if let nextMarker = marker(next.text), nextMarker.indent <= first.indent {
                            break
                        }
                        guard indent(next.text) > first.indent else { break }
                        content.append(Line(id: next.id, text: String(next.text.dropFirst(min(current.contentColumn, indent(next.text)))))); index += 1
                    }
                    items.append(Item(id: itemID, blocks: parse(content, depth: depth + 1, shortened: &shortened)))
                    if index < lines.count, trim(lines[index].text).isEmpty,
                       index + 1 < lines.count, let next = marker(lines[index + 1].text), next.indent == first.indent, next.ordered == first.ordered
                    {
                        index += 1
                    }
                }
                result.append(Block(id: line.id, kind: .list(ordered: first.ordered, start: first.start, items: items)))
            } else if index + 1 < lines.count, indentedCode(lines[index + 1].text) == nil,
                      let headers = tableHeader(line.text, delimiter: lines[index + 1].text)
            {
                index += 2
                var rows: [[String]] = []
                while index < lines.count, !trim(lines[index].text).isEmpty, lines[index].text.contains("|") {
                    let cells = splitCells(lines[index].text)
                    if rows.count < 100 {
                        rows.append(Array((cells + Array(repeating: "", count: max(0, headers.count - cells.count))).prefix(headers.count)))
                    } else {
                        shortened = true
                    }
                    index += 1
                }
                result.append(Block(id: line.id, kind: .table(headers: headers, rows: rows)))
            } else if index + 1 < lines.count, indentedCode(lines[index + 1].text) == nil, let level = setext(lines[index + 1].text) {
                result.append(Block(id: line.id, kind: .heading(level, text))); index += 2
            } else {
                var paragraph = [line.text]
                index += 1
                while index < lines.count {
                    let next = trim(lines[index].text)
                    // Indentation continues an existing paragraph; it cannot start code here.
                    if !next.isEmpty, indentedCode(lines[index].text) != nil {
                        paragraph.append(lines[index].text); index += 1; continue
                    }
                    if next.isEmpty || fence(next) != nil || rule(next) || heading(next) != nil || next.hasPrefix(">") || marker(lines[index].text) != nil {
                        break
                    }
                    if index + 1 < lines.count, indentedCode(lines[index + 1].text) == nil,
                       tableHeader(lines[index].text, delimiter: lines[index + 1].text) != nil || setext(lines[index + 1].text) != nil
                    {
                        break
                    }
                    paragraph.append(lines[index].text); index += 1
                }
                result.append(Block(id: line.id, kind: .paragraph(paragraph.joined(separator: "\n"))))
            }
        }
        return result
    }

    private static func indentedCode(_ text: String) -> String? {
        var column = 0
        var index = text.startIndex
        while column < 4, index < text.endIndex {
            switch text[index] {
            case " ": column += 1
            case "\t": column += 4 - column % 4
            default: return nil
            }
            index = text.index(after: index)
        }
        guard column >= 4 else { return nil }
        return String(text[index...])
    }

    private static func fence(_ text: String) -> (character: Character, count: Int)? {
        guard let first = text.first, first == "`" || first == "~" else { return nil }
        let count = text.prefix { $0 == first }.count
        guard count >= 3, first != "`" || !text.dropFirst(count).contains("`") else { return nil }
        return (first, count)
    }

    private static func rule(_ text: String) -> Bool {
        let characters = text.filter { !$0.isWhitespace }
        guard characters.count >= 3, let first = characters.first, ["-", "*", "_"].contains(first) else { return false }
        return characters.allSatisfy { $0 == first }
    }

    private static func heading(_ text: String) -> (Int, String)? {
        let count = text.prefix { $0 == "#" }.count
        guard (1 ... 6).contains(count), text.dropFirst(count).first?.isWhitespace == true else { return nil }
        return (count, trim(String(text.dropFirst(count))))
    }

    private static func setext(_ text: String) -> Int? {
        let text = trim(text)
        guard !text.isEmpty else { return nil }
        if text.allSatisfy({ $0 == "=" }) {
            return 1
        }
        if text.allSatisfy({ $0 == "-" }) {
            return 2
        }
        return nil
    }

    private static func marker(_ text: String) -> Marker? {
        let indentation = indent(text)
        let body = text.dropFirst(indentation)
        let prefix: Int
        let number: Int?
        if let first = body.first, ["-", "*", "+"].contains(first) {
            prefix = 1; number = nil
        } else {
            let digits = body.prefix { $0.isASCII && $0.isNumber }
            guard !digits.isEmpty, digits.count <= 9, let value = Int(digits), let punctuation = body.dropFirst(digits.count).first, punctuation == "." || punctuation == ")" else { return nil }
            prefix = digits.count + 1; number = value
        }
        let remainder = body.dropFirst(prefix)
        guard remainder.first?.isWhitespace == true else { return nil }
        let spaces = remainder.prefix { $0.isWhitespace }.count
        return Marker(ordered: number != nil, start: number ?? 1, indent: indentation, contentColumn: indentation + prefix + spaces, text: String(remainder.dropFirst(spaces)))
    }

    private static func tableHeader(_ line: String, delimiter: String) -> [String]? {
        guard line.contains("|") else { return nil }
        let headers = splitCells(line)
        let cells = splitCells(delimiter)
        guard !headers.isEmpty, headers.count <= 16, cells.count == headers.count,
              cells.allSatisfy({ cell in
                  let value = cell.trimmingCharacters(in: CharacterSet(charactersIn: ":"))
                  return value.count >= 3 && value.allSatisfy { $0 == "-" }
              }) else { return nil }
        return headers
    }

    private static func splitCells(_ line: String) -> [String] {
        var cells: [String] = []
        var cell = ""
        var escaped = false
        var ticks = 0
        let characters = Array(trim(line))
        var index = 0
        while index < characters.count {
            let character = characters[index]
            if escaped {
                if character != "|" {
                    cell.append("\\")
                }
                cell.append(character); escaped = false; index += 1; continue
            }
            if character == "\\" {
                escaped = true; index += 1; continue
            }
            if character == "`" {
                var count = 1
                while index + count < characters.count, characters[index + count] == "`" {
                    count += 1
                }
                if ticks == 0 {
                    ticks = count
                } else if ticks == count {
                    ticks = 0
                }
                cell += String(repeating: "`", count: count); index += count; continue
            }
            if character == "|", ticks == 0 {
                cells.append(trim(cell)); cell = ""
            } else {
                cell.append(character)
            }
            index += 1
        }
        if escaped {
            cell.append("\\")
        }
        cells.append(trim(cell))
        if characters.first == "|", cells.first == "" {
            cells.removeFirst()
        }
        if characters.last == "|", cells.last == "" {
            cells.removeLast()
        }
        return cells
    }
}
