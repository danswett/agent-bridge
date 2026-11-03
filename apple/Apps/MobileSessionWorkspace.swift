#if os(iOS)
    import SwiftUI

    /// The detail column can be narrow even on a large iPad when the session list
    /// is open. Measure this column, rather than choosing from the device model.
    struct MobileSessionWorkspace<Conversation: View, Inspector: View>: View {
        let entry: SessionEntry
        let store: BridgeStore
        @Binding var showingInspector: Bool
        @ViewBuilder var conversation: () -> Conversation
        @ViewBuilder var inspector: () -> Inspector

        @Environment(\.horizontalSizeClass) private var sizeClass
        @Environment(\.dynamicTypeSize) private var typeSize
        @State private var inspectorHidden = false

        var body: some View {
            GeometryReader { geometry in
                let canSplit = sizeClass == .regular && geometry.size.width >= 690 && !typeSize.isAccessibilitySize
                let sideBySide = canSplit && !inspectorHidden
                HStack(spacing: 0) {
                    VStack(spacing: 0) {
                        conversation()
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                        if let host = entry.host, !entry.ended {
                            Divider()
                            composer(host: host, availableHeight: geometry.size.height)
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    if sideBySide {
                        Divider()
                        inspectorContent(inline: true)
                            .frame(width: min(360, max(275, geometry.size.width * 0.34)))
                    }
                }
                .toolbar {
                    ToolbarItem(placement: .topBarTrailing) {
                        Button {
                            if canSplit {
                                inspectorHidden.toggle()
                            } else {
                                showingInspector = true
                            }
                        } label: {
                            Label(sideBySide ? "Hide session inspector" : "Session inspector", systemImage: "sidebar.right")
                        }
                        .accessibilityIdentifier("session-inspector-button")
                    }
                }
                .sheet(isPresented: $showingInspector) {
                    NavigationStack {
                        inspectorContent(inline: false)
                            .navigationTitle("Session inspector")
                            .navigationBarTitleDisplayMode(.inline)
                            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { showingInspector = false } } }
                    }
                    .presentationDragIndicator(.visible)
                }
                .onChange(of: canSplit) { _, wide in
                    if wide {
                        showingInspector = false
                    }
                }
            }
        }

        private func composer(host: HostStore, availableHeight: CGFloat) -> some View {
            // Keep the field reachable above the keyboard. Receipts, large text,
            // staged files and long forms can grow beyond the available height.
            let limit = max(110, min(350, availableHeight * 0.48))
            return SessionActions(sessionID: entry.session.id, store: store, host: host, presentation: .composer)
                .id(entry.id)
                .frame(maxHeight: limit, alignment: .topLeading)
                .fixedSize(horizontal: false, vertical: true)
                .background(.background)
        }

        private func inspectorContent(inline: Bool) -> some View {
            VStack(alignment: .leading, spacing: 0) {
                if inline {
                    Label("Session inspector", systemImage: "sidebar.right")
                        .font(.headline)
                        .padding(.horizontal).padding(.vertical, 12)
                        .accessibilityAddTraits(.isHeader)
                    Divider()
                }
                inspector().frame(maxWidth: .infinity, maxHeight: .infinity)
                if let host = entry.host, !entry.ended {
                    Divider()
                    SessionActions(sessionID: entry.session.id, store: store, host: host, presentation: .inspector)
                        .id(entry.id)
                        .padding(.horizontal).padding(.vertical, 10)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }
#endif
