import SwiftUI

struct HostWorkingIndicator: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.scenePhase) private var scenePhase
    @State private var visible = false
    @State private var startedAt = Date()

    private var label: some View {
        Text("Host is working…")
            .font(.body.weight(.medium))
    }

    var body: some View {
        label
            .foregroundStyle(.secondary)
            .overlay {
                if !reduceMotion, scenePhase == .active, visible {
                    TimelineView(.animation(minimumInterval: 1.0 / 30)) { context in
                        let phase = context.date.timeIntervalSince(startedAt).truncatingRemainder(dividingBy: 1.8) / 1.8
                        label
                            .foregroundStyle(.tint)
                            .mask {
                                GeometryReader { geometry in
                                    let band = geometry.size.width * 0.45
                                    LinearGradient(colors: [.clear, .white, .clear],
                                                   startPoint: .leading, endPoint: .trailing)
                                        .frame(width: band)
                                        .offset(x: -band + (geometry.size.width + band) * phase)
                                }
                            }
                    }
                    .accessibilityHidden(true)
                    .allowsHitTesting(false)
                }
            }
            .padding(.vertical, 8)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Host is working")
            .accessibilityIdentifier("host-working-indicator")
            .onAppear {
                startedAt = Date()
                visible = true
            }
            .onScrollVisibilityChange(threshold: 0.01) { visible = $0 }
            .onDisappear { visible = false }
    }
}
