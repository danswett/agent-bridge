import AppKit
import SwiftUI

@main
struct MacAttachmentMenuChecks {
    @MainActor
    static func main() {
        _ = NSApplication.shared
        let bounds = NSRect(x: 0, y: 0, width: 36, height: 36)
        let size = NSSize(width: 400, height: 160)
        let up = MacAttachmentMenu.Presenter.origin(bounds: bounds, menuSize: size, flipped: false)
        precondition(up.y - size.height > bounds.maxY, "The entire menu must be above the button.")
        let flipped = MacAttachmentMenu.Presenter.origin(bounds: bounds, menuSize: size, flipped: true)
        precondition(flipped.y + size.height < bounds.minY, "Flipped coordinates must still open upward.")

        var actions: [Int] = []
        let presenter = MacAttachmentMenu.Presenter()
        presenter.enabled = true
        presenter.actions = [1: { actions.append(1) }, 2: { actions.append(2) }, 3: { actions.append(3) }]
        let menu = presenter.makeMenu(hint: "Attachment limit")
        precondition(menu.items.prefix(3).map(\.title) == ["Attach File…", "Choose Photo…", "Paste Photo"])
        precondition(menu.items.prefix(3).allSatisfy { !$0.isHidden && $0.isEnabled }, "Every attachment action must remain visible.")
        precondition(menu.items.last?.isEnabled == false && menu.items.last?.action == nil)
        for item in menu.items.prefix(3) {
            presenter.select(item)
            precondition(actions.count == item.tag - 1, "Picking an item must not open a picker before the menu closes.")
            presenter.finishSelection(accepted: true)
        }
        precondition(actions == [1, 2, 3])
        presenter.select(menu.items[0])
        presenter.finishSelection(accepted: false)
        presenter.select(menu.items[0])
        presenter.enabled = false
        presenter.finishSelection(accepted: true)
        precondition(actions == [1, 2, 3], "Cancelled or disabled menus cannot invoke attachment actions.")

        let control = MacAttachmentMenu(disabled: false, hint: "", help: "",
                                       attachFile: {}, choosePhoto: {}, pastePhoto: {})
        let hosting = NSHostingView(rootView: control)
        hosting.frame = bounds
        hosting.layoutSubtreeIfNeeded()
        func containsPopup(_ view: NSView) -> Bool {
            view is NSPopUpButton || view.subviews.contains(where: containsPopup)
        }
        precondition(!containsPopup(hosting), "The plus must not acquire popup-button styling or hidden-item behavior.")
        print("PASS: upward geometry, three visible actions, plain button, deferred action and disabled/cancel guards.")
    }
}
