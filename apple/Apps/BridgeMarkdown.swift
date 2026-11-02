import AgentBridgeAppleCore
import SwiftUI

/// Markdown is rendered as native text. Images and raw HTML never fetch or execute.
struct BridgeMarkdown: View {
    private let document: BridgeMarkdownDocument

    init(_ source: String) {
        document = BridgeMarkdownDocument(source)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            ForEach(document.blocks) { MarkdownBlockView(block: $0) }
            if document.isTruncated {
                Text("Additional content was shortened for display.").font(.caption).foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct MarkdownBlockView: View {
    let block: BridgeMarkdownDocument.Block

    var body: some View {
        switch block.kind {
        case let .paragraph(text):
            Text(markdownInline(text))
        case let .heading(level, text):
            Text(markdownInline(text))
                .font(level == 1 ? .title2 : level == 2 ? .title3 : .headline)
                .accessibilityAddTraits(.isHeader)
        case let .code(text):
            ScrollView(.horizontal) { Text(text).font(.body.monospaced()).padding(10) }
                .background(.quaternary, in: .rect(cornerRadius: 6))
        case let .quote(blocks):
            HStack(alignment: .top, spacing: 10) {
                RoundedRectangle(cornerRadius: 2).fill(.tertiary).frame(width: 3)
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(blocks) { MarkdownBlockView(block: $0) }
                }
            }
            .fixedSize(horizontal: false, vertical: true)
            .foregroundStyle(.secondary)
        case let .list(ordered, start, items):
            VStack(alignment: .leading, spacing: 8) {
                ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Text(ordered ? "\(start + index)." : "•").monospacedDigit().frame(minWidth: 20, alignment: .trailing)
                        VStack(alignment: .leading, spacing: 8) {
                            ForEach(item.blocks) { MarkdownBlockView(block: $0) }
                        }
                    }
                }
            }
        case .rule:
            Divider().accessibilityLabel("Separator")
        case let .table(headers, rows):
            ScrollView(.horizontal) {
                Grid(alignment: .topLeading, horizontalSpacing: 16, verticalSpacing: 8) {
                    GridRow {
                        ForEach(headers.indices, id: \.self) { index in
                            cell(headers[index]).fontWeight(.semibold).accessibilityAddTraits(.isHeader)
                        }
                    }
                    Divider().gridCellUnsizedAxes(.horizontal)
                    ForEach(rows.indices, id: \.self) { row in
                        GridRow {
                            ForEach(headers.indices, id: \.self) { column in cell(rows[row][column]) }
                        }
                    }
                }
                .padding(10)
            }
            .background(.quaternary.opacity(0.5), in: .rect(cornerRadius: 6))
        }
    }

    private func cell(_ text: String) -> some View {
        Text(markdownInline(text)).frame(maxWidth: 260, alignment: .leading).fixedSize(horizontal: false, vertical: true)
    }
}

private func markdownInline(_ text: String) -> AttributedString {
    var value = (try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace))) ?? AttributedString(text)
    // A host may supply text, but it cannot turn a file or script URL into a native action.
    for run in value.runs {
        if let link = run.link, !["https", "http", "mailto"].contains(link.scheme?.lowercased() ?? "") {
            value[run.range].link = nil
        }
    }
    return value
}
