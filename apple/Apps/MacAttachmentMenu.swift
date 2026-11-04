#if os(macOS)
    import AppKit
    import SwiftUI

    @MainActor
    struct MacAttachmentMenu: View {
        let disabled: Bool
        let hint: String
        let help: String
        let attachFile: () -> Void
        let choosePhoto: () -> Void
        let pastePhoto: () -> Void
        @State private var presenter = Presenter()
        @FocusState private var focused: Bool

        var body: some View {
            Button { presenter.show(hint: hint) } label: {
                Label("Add attachment", systemImage: "plus")
                    .labelStyle(.iconOnly)
                    .font(.title2)
                    .frame(width: 36, height: 36)
                    .contentShape(.circle)
            }
            .buttonStyle(.plain)
            .foregroundStyle(.primary)
            .focused($focused)
            .focusEffectDisabled()
            .overlay {
                if focused {
                    Circle().strokeBorder(Color.accentColor.opacity(0.7), lineWidth: 1.5)
                        .padding(2).allowsHitTesting(false)
                }
            }
            .background {
                Anchor(presenter: presenter, disabled: disabled,
                       actions: [1: attachFile, 2: choosePhoto, 3: pastePhoto])
                    .allowsHitTesting(false)
            }
            .disabled(disabled)
            .help(help)
            .accessibilityIdentifier("composer-attachment-menu")
        }

        @MainActor
        final class Presenter: NSObject {
            weak var anchor: NSView?
            var enabled = false
            var actions: [Int: () -> Void] = [:]
            private var menu: NSMenu?
            private var selected: Int?

            static func origin(bounds: NSRect, menuSize: NSSize, flipped: Bool) -> NSPoint {
                NSPoint(x: bounds.minX,
                        y: flipped ? bounds.minY - menuSize.height - 4 : bounds.maxY + menuSize.height + 4)
            }

            func makeMenu(hint: String) -> NSMenu {
                let menu = NSMenu(title: String(localized: "Attachments"))
                menu.autoenablesItems = false
                for (title, symbol, tag) in [
                    (String(localized: "Attach File…"), "paperclip", 1),
                    (String(localized: "Choose Photo…"), "photo", 2),
                    (String(localized: "Paste Photo"), "clipboard", 3),
                ] {
                    let item = NSMenuItem(title: title, action: #selector(select(_:)), keyEquivalent: "")
                    item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)
                    item.tag = tag
                    item.target = self
                    item.isEnabled = enabled
                    menu.addItem(item)
                }
                menu.addItem(.separator())
                let note = NSMenuItem(title: hint, action: nil, keyEquivalent: "")
                note.isEnabled = false
                menu.addItem(note)
                return menu
            }

            func show(hint: String) {
                guard enabled, menu == nil, let anchor, anchor.window != nil else { return }
                let popup = makeMenu(hint: hint)
                menu = popup
                selected = nil
                popup.update()
                // NSMenu's location is its top-left corner, not an advisory preferred edge.
                let point = Self.origin(bounds: anchor.bounds, menuSize: popup.size, flipped: anchor.isFlipped)
                let accepted = popup.popUp(positioning: nil, at: point, in: anchor)
                menu = nil
                finishSelection(accepted: accepted)
            }

            @objc func select(_ item: NSMenuItem) {
                guard enabled, actions[item.tag] != nil else { return }
                selected = item.tag
            }

            func finishSelection(accepted: Bool) {
                let choice = selected
                selected = nil
                guard accepted, enabled, let choice else { return }
                actions[choice]?()
            }

            func cancel() {
                selected = nil
                menu?.cancelTracking()
            }
        }

        private struct Anchor: NSViewRepresentable {
            let presenter: Presenter
            let disabled: Bool
            let actions: [Int: () -> Void]

            func makeCoordinator() -> Presenter { presenter }

            func makeNSView(context: Context) -> NSView {
                let view = NSView()
                view.setAccessibilityElement(false)
                presenter.anchor = view
                updateNSView(view, context: context)
                return view
            }

            func updateNSView(_ view: NSView, context: Context) {
                presenter.anchor = view
                presenter.enabled = !disabled
                presenter.actions = actions
                if disabled { presenter.cancel() }
            }

            static func dismantleNSView(_ view: NSView, coordinator: Presenter) {
                coordinator.cancel()
                coordinator.anchor = nil
                coordinator.actions = [:]
            }
        }
    }
#endif
