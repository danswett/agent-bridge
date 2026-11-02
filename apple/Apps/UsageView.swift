import AgentBridgeAppleCore
import SwiftUI

struct UsageView: View {
    var report: JSONValue?
    var machineName: String?
    var status: String?
    var onRefresh: (@MainActor () async -> Void)?

    @State private var isRefreshing = false
    @State private var refreshID: UUID?

    var body: some View {
        TimelineView(.periodic(from: .now, by: 30)) { context in
            let allowances = report.map { SessionPresentation.usage(report: $0, now: context.date) } ?? []
            List {
                if let status {
                    Text(status).foregroundStyle(.secondary)
                }
                if allowances.isEmpty {
                    ContentUnavailableView("No usage readings", systemImage: "chart.bar",
                                           description: Text("Usage appears when a connected host reports an agent’s allowance."))
                }
                ForEach(allowances) { allowance in
                    Section {
                        UsageAllowanceView(allowance: allowance, now: context.date, defaultMachine: machineName)
                    } header: {
                        Text(allowance.label)
                    }
                }
            }
        }
        .navigationTitle("Usage")
        .toolbar {
            if onRefresh != nil {
                Button("Refresh", systemImage: "arrow.clockwise") { refreshID = UUID() }
                    .disabled(isRefreshing)
            }
        }
        .refreshable { await refresh() }
        .task(id: refreshID) {
            guard refreshID != nil else { return }
            await refresh()
        }
    }

    private func refresh() async {
        guard let onRefresh, !isRefreshing else { return }
        isRefreshing = true
        await onRefresh()
        isRefreshing = false
    }
}

private struct UsageAllowanceView: View {
    let allowance: SessionPresentation.UsageAllowance
    let now: Date
    let defaultMachine: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if let account = allowance.account {
                Text(account).font(.headline)
            }
            if let plan = allowance.plan {
                Text(plan).font(.caption).foregroundStyle(.secondary)
            }
            ForEach(allowance.windows) { window in
                VStack(alignment: .leading, spacing: 5) {
                    HStack {
                        Text(window.label)
                        Spacer()
                        Text(window.percent.formatted(.number.precision(.fractionLength(0 ... 1))) + "%")
                            .monospacedDigit().foregroundStyle(tint(window.severity))
                    }
                    ProgressView(value: window.progress, total: 100)
                        .tint(tint(window.severity))
                        .accessibilityLabel(window.label)
                        .accessibilityValue("\(window.percent.formatted()) percent used")
                    if let used = window.used, let limit = window.limit {
                        Text("\(used.formatted()) / \(limit.formatted())\(window.unit.map { " " + $0 } ?? "")")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    if let pace = window.paceText, let elapsed = window.elapsedPercent {
                        Text("Even pace \(elapsed.formatted(.number.precision(.fractionLength(0))))% · \(pace)")
                            .font(.caption).foregroundStyle(window.aheadOfPace ? .orange : .secondary)
                    }
                    if let resets = window.resetsAt {
                        Text(SessionPresentation.reset(resets, now: now)).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            if let error = allowance.error {
                Label(error, systemImage: "exclamationmark.triangle").font(.callout).foregroundStyle(.orange)
            }
            VStack(alignment: .leading, spacing: 3) {
                Text(SessionPresentation.age(allowance.measuredAt, now: now))
                if let machine = allowance.readBy ?? defaultMachine {
                    Text("Read on \(machine)")
                }
            }
            .font(.caption)
            .foregroundStyle(allowance.isStale ? .orange : .secondary)
        }
        .padding(.vertical, 4)
    }

    private func tint(_ severity: SessionPresentation.Severity) -> Color {
        switch severity {
        case .normal: .accentColor
        case .warning: .orange
        case .critical: .red
        }
    }
}
