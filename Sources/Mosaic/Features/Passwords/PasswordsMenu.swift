import AppKit

/// Password items for the Edit menu (area: passwords). `AppDelegate` adds them with
/// `model.passwords.menu.addItems(to:)`.
@MainActor
final class PasswordsMenu: NSObject, NSMenuItemValidation {
    weak var passwords: PasswordManager?
    weak var model: BrowserModel?

    func addItems(to menu: NSMenu, model: BrowserModel) {
        self.model = model
        menu.addItem(.separator())
        // ⌘\ works inside Gecko pages too: the page script forwards it (see passwords-page.js).
        menu.addItem(item("Fill Password", #selector(fillPassword), "\\"))
        menu.addItem(item("Suggest Strong Password", #selector(suggestPassword), ""))
        menu.addItem(item("Passwords…", #selector(openPasswords), ""))
    }

    private func item(_ title: String, _ action: Selector, _ key: String) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.keyEquivalentModifierMask = [.command]
        item.target = self
        return item
    }

    @objc private func fillPassword() { passwords?.fill(in: model?.selectedTab) }
    @objc private func suggestPassword() { passwords?.offerGeneratedPassword(in: model?.selectedTab) }
    @objc private func openPasswords() { passwords?.openSettings() }

    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        switch menuItem.action {
        case #selector(fillPassword):
            return !(passwords?.fillable(in: model?.selectedTab).isEmpty ?? true)
        case #selector(suggestPassword):
            return passwords?.origin(of: model?.selectedTab) != nil
        default:
            return true
        }
    }
}
