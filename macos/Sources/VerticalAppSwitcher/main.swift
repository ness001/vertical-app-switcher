import AppKit
import Carbon
import ServiceManagement
import SwiftUI

final class OverlayPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private var panel: OverlayPanel!
    private var statusItem: NSStatusItem!
    private let shortcut = GlobalShortcut()
    private var model: SwitcherModel!
    private var keyMonitor: Any?
    private var clickMonitor: Any?
    private var scrollMonitor: Any?
    private var lastScrollMove = Date.distantPast
    private var resignObserver: Any?
    private var shortcutObserver: Any?
    private var setupObserver: Any?
    private var shortcutSetup: ShortcutSetupController?
    private var loginItem: NSMenuItem?
    private var shortcutMenuItem: NSMenuItem?
    private var appearanceMenu: NSMenu?
    private var isShowing = false
    private var commandObserver: NSObjectProtocol?
    private var instanceLock: FileHandle?
    private var updater: UpdatesController?
    private let demo = CommandLine.arguments.contains("--demo")

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        guard demo || acquireInstance() else { NSApp.terminate(nil); return }
        let store: StateStore = demo ? StateStore(directory: FileManager.default.temporaryDirectory.appendingPathComponent("VerticalAppSwitcher-demo-\(UUID().uuidString)")) : .standard
        let firstRun = !demo && !FileManager.default.fileExists(atPath: store.file.path)
        model = SwitcherModel(store: store, demo: demo)
        model.dismiss = { [weak self] in self?.hide() }
        panel = OverlayPanel(contentRect: NSRect(x: 0, y: 0, width: 520, height: model.overlayHeight),
            styleMask: [.borderless, .nonactivatingPanel, .fullSizeContentView], backing: .buffered, defer: false)
        panel.title = "Vertical App Switcher"
        panel.isReleasedWhenClosed = false
        panel.isFloatingPanel = true
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.isMovableByWindowBackground = true
        panel.hidesOnDeactivate = false
        panel.delegate = self
        panel.contentView = NSHostingView(rootView: SwitcherView(model: model))
        panel.appearance = model.state.appearance.appKitAppearance
        setupStatusItem()
        shortcut.onPress = { [weak self] in self?.toggle() }
        let shortcutUnavailable = !demo && !shortcut.register(model.state.shortcut)
        if shortcutUnavailable {
            model.message = "The global shortcut is unavailable. Choose another in shortcut setup."
        }
        shortcutObserver = NotificationCenter.default.addObserver(forName: .changeShortcut, object: nil, queue: .main) { [weak self] notification in
            guard let value = notification.object as? Shortcut else { return }
            self?.changeShortcut(value)
        }
        setupObserver = NotificationCenter.default.addObserver(forName: .setupShortcut, object: nil, queue: .main) { [weak self] _ in
            self?.configureShortcut()
        }
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, self.isShowing, event.window === self.panel else { return event }
            return self.handleKey(event) ? nil : event
        }
        scrollMonitor = NSEvent.addLocalMonitorForEvents(matching: .scrollWheel) { [weak self] event in
            guard let self, self.isShowing, event.window === self.panel,
                  !self.model.searchFocused, !self.model.confirmingAction,
                  abs(event.scrollingDeltaY) > 0.1 else { return event }
            if Date().timeIntervalSince(self.lastScrollMove) >= 0.1 {
                self.model.move(event.scrollingDeltaY < 0 ? 1 : -1)
                self.lastScrollMove = Date()
            }
            return nil
        }
        clickMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in self?.hide() }
        resignObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] _ in
            self?.hide()
        }
        model.start()
        if !demo {
            updater?.start()
            UpdatesInstaller.confirmStartup()
        }
        let launchEvent = NSAppleEventManager.shared().currentAppleEvent
        let launchedAtLogin = launchEvent?.paramDescriptor(forKeyword: AEKeyword(keyAEPropData))?
            .paramDescriptor(forKeyword: AEKeyword(keyAELaunchedAsLogInItem))?.booleanValue == true
            || launchEvent?.paramDescriptor(forKeyword: AEKeyword(keyAELaunchedAsLogInItem))?.booleanValue == true
        if demo || (!CommandLine.arguments.contains("--background") && !launchedAtLogin) || !WindowCatalog.isTrusted { show() }
        if shortcutUnavailable || firstRun { configureShortcut() }
    }

    func applicationWillTerminate(_ notification: Notification) {
        updater?.applicationWillTerminate()
        if !demo { DiagnosticLog.shared.write("Application stopped") }
        try? instanceLock?.close()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        show()
        return true
    }

    @MainActor
    private func setupStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = NSImage(systemSymbolName: "rectangle.stack", accessibilityDescription: "Vertical App Switcher")
        let menu = NSMenu()
        menu.addItem(withTitle: "Open Switcher", action: #selector(openOverlay), keyEquivalent: "")
        menu.addItem(.separator())
        let shortcutItem = NSMenuItem(title: "Shortcut…", action: #selector(configureShortcut), keyEquivalent: "")
        menu.addItem(shortcutItem)
        shortcutMenuItem = shortcutItem
        updateShortcutMenuLabel()
        let appearanceItem = NSMenuItem(title: "Appearance", action: nil, keyEquivalent: "")
        let appearances = NSMenu(title: "Appearance")
        for value in SwitcherAppearance.allCases {
            let item = NSMenuItem(title: value.title, action: #selector(changeAppearance(_:)), keyEquivalent: "")
            item.representedObject = value.rawValue
            item.target = self
            item.state = value == model.state.appearance ? .on : .off
            appearances.addItem(item)
        }
        appearanceItem.submenu = appearances
        appearanceMenu = appearances
        menu.addItem(appearanceItem)
        let login = NSMenuItem(title: "Launch at Login", action: #selector(toggleLogin), keyEquivalent: "")
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        menu.addItem(login)
        loginItem = login
        if !WindowCatalog.isTrusted {
            menu.addItem(withTitle: "Grant Accessibility Access…", action: #selector(accessibilitySettings), keyEquivalent: "")
        }
        menu.addItem(.separator())
        menu.addItem(withTitle: "About…", action: #selector(about), keyEquivalent: "")
        menu.addItem(withTitle: "Quit", action: #selector(quit), keyEquivalent: "q")
        for item in menu.items { item.target = self }
        statusItem.menu = menu
        if !demo {
            updater = UpdatesController(menu: menu, canShow: { [weak self] in
                self?.model.confirmingAction == false && NSApp.modalWindow == nil
            }, requestExit: { [weak self] in
                self?.hide()
                NSApp.terminate(nil)
            })
        }
    }

    private func updateShortcutMenuLabel() {
        guard let item = shortcutMenuItem else { return }
        item.attributedTitle = nil
        item.keyEquivalent = ""
        item.keyEquivalentModifierMask = []
        guard let combination = model.state.shortcut.combination else { return }
        let flags = combination.modifiers
        var modifiers: NSEvent.ModifierFlags = []
        if flags & UInt32(controlKey) != 0 { modifiers.insert(.control) }
        if flags & UInt32(optionKey) != 0 { modifiers.insert(.option) }
        if flags & UInt32(shiftKey) != 0 { modifiers.insert(.shift) }
        if flags & UInt32(cmdKey) != 0 { modifiers.insert(.command) }
        item.keyEquivalentModifierMask = modifiers
        item.keyEquivalent = combination.keyCode == UInt32(kVK_Space) ? " "
            : String(model.state.shortcut.title.drop(while: { "⌃⌥⇧⌘".contains($0) })).lowercased()
    }

    @discardableResult
    private func changeShortcut(_ value: Shortcut) -> Bool {
        if value == model.state.shortcut {
            guard demo || shortcut.register(value) else { return false }
            let saved = model.setShortcut(value)
            if saved { updateShortcutMenuLabel() }
            return saved
        }
        guard demo || shortcut.register(value) else {
            if !demo { DiagnosticLog.shared.write("Shortcut registration failed for \(value.title)") }
            model.message = "\(value.title) is already reserved. The previous shortcut is still active."
            show()
            return false
        }
        let previous = model.state.shortcut
        guard model.setShortcut(value) else {
            if !demo { _ = shortcut.register(previous) }
            return false
        }
        updateShortcutMenuLabel()
        if !demo { DiagnosticLog.shared.write("Shortcut registered: \(value.title)") }
        return true
    }

    @objc private func changeAppearance(_ sender: NSMenuItem) {
        guard let rawValue = sender.representedObject as? String,
              let value = SwitcherAppearance(rawValue: rawValue) else { return }
        guard model.setAppearance(value) else { show(); return }
        panel.appearance = value.appKitAppearance
        for item in appearanceMenu?.items ?? [] {
            item.state = item.representedObject as? String == value.rawValue ? .on : .off
        }
    }

    @objc private func configureShortcut() {
        hide()
        shortcutSetup = ShortcutSetupController(shortcut: shortcut, current: model.state.shortcut) { [weak self] value in
            guard let self else { return false }
            return self.changeShortcut(value)
        }
        shortcutSetup?.show()
    }

    private func acquireInstance() -> Bool {
        do {
            let directory = StateStore.standard.directory
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let url = directory.appendingPathComponent("instance.lock")
            if !FileManager.default.fileExists(atPath: url.path) { FileManager.default.createFile(atPath: url.path, contents: Data()) }
            let handle = try FileHandle(forUpdating: url)
            if flock(handle.fileDescriptor, LOCK_EX | LOCK_NB) != 0 {
                DistributedNotificationCenter.default().postNotificationName(.init("VerticalAppSwitcher.command"), object: NSUserName(),
                    userInfo: ["command": CommandLine.arguments.contains("--shutdown") ? "shutdown" : "show"], deliverImmediately: true)
                try handle.close()
                return false
            }
            if CommandLine.arguments.contains("--shutdown") { try handle.close(); return false }
            instanceLock = handle
            commandObserver = DistributedNotificationCenter.default().addObserver(forName: .init("VerticalAppSwitcher.command"), object: NSUserName(), queue: .main) { [weak self] event in
                if event.userInfo?["command"] as? String == "shutdown" { NSApp.terminate(nil) }
                else { self?.show() }
            }
            DiagnosticLog.shared.write("Application started")
            return true
        } catch {
            let alert = NSAlert()
            alert.messageText = "Unable to start Vertical App Switcher"
            alert.informativeText = error.localizedDescription
            alert.runModal()
            return false
        }
    }

    @objc private func openOverlay() { show() }
    @objc private func accessibilitySettings() { openAccessibilitySettings() }
    @objc private func quit() { NSApp.terminate(nil) }
    @objc private func about() {
        let alert = NSAlert()
        alert.messageText = "Vertical App Switcher"
        alert.informativeText = "A native macOS window switcher.\n\nCopyright 2026 ness001. GNU GPL 3.0 only.\nSee LICENSE and NOTICE inside the app’s Resources folder.\n\nNative keyboard and mouse controls. Controller input is intentionally unsupported. Uses Accessibility and dynamically loaded macOS Spaces interfaces. Compatibility with future macOS releases is best-effort."
        alert.runModal()
    }

    @objc private func toggleLogin() {
        guard !demo else { return }
        do {
            if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() }
            else { try SMAppService.mainApp.register() }
            loginItem?.state = SMAppService.mainApp.status == .enabled ? .on : .off
            if SMAppService.mainApp.status == .requiresApproval { SMAppService.openSystemSettingsLoginItems() }
        } catch {
            model.message = "Launch at login could not be changed: \(error.localizedDescription)"
            show()
        }
    }

    private func toggle() { isShowing ? hide() : show() }

    private func show() {
        guard !isShowing else { return }
        model.prepareToOpen()
        let point = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { $0.frame.contains(point) } ?? NSScreen.main
        if let frame = screen?.visibleFrame {
            model.overlayHeight = min(680, max(360, frame.height - 40))
            panel.setContentSize(NSSize(width: 520, height: model.overlayHeight))
            panel.setFrameOrigin(NSPoint(x: frame.midX - panel.frame.width / 2, y: frame.midY - panel.frame.height / 2))
        }
        isShowing = true
        model.overlayVisible = true
        panel.makeKeyAndOrderFront(nil)
    }

    private func hide() {
        guard isShowing, !model.confirmingAction else { return }
        isShowing = false
        model.overlayVisible = false
        panel.orderOut(nil)
    }

    func windowDidResignKey(_ notification: Notification) { hide() }

    private func handleKey(_ event: NSEvent) -> Bool {
        guard !model.confirmingAction else { return false }
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        let command = flags.contains(.command)
        let character = event.charactersIgnoringModifiers?.lowercased() ?? ""
        if event.keyCode == UInt16(kVK_Escape) { hide(); return true }
        if command && character == "f" { model.focusSearch(); return true }
        if model.searchFocused, command, ["a", "c", "v", "x", "z"].contains(character),
           let editor = panel.firstResponder as? NSTextView {
            switch character {
            case "a": editor.selectAll(nil)
            case "c": editor.copy(nil)
            case "v": editor.paste(nil)
            case "x": editor.cut(nil)
            case "z":
                if flags.contains(.shift) { editor.undoManager?.redo() }
                else { editor.undoManager?.undo() }
            default: return false
            }
            return true
        }
        if command && !flags.contains(.option) && !flags.contains(.control), let number = Int(character), (1...9).contains(number) {
            model.activateNumber(number - 1); return true
        }
        let navigation = !flags.contains(.command) && !flags.contains(.option) && !flags.contains(.control)
        guard navigation else { return false }
        if event.keyCode == UInt16(kVK_DownArrow) { model.move(1); return true }
        if event.keyCode == UInt16(kVK_UpArrow) { model.move(-1); return true }
        if event.keyCode == UInt16(kVK_Tab) {
            if model.searchFocused { model.searchFocused = false }
            else { model.move(flags.contains(.shift) ? -1 : 1) }
            return true
        }
        guard !model.searchFocused else { return false }
        switch Int(event.keyCode) {
        case kVK_LeftArrow: model.moveTab(-1)
        case kVK_RightArrow: model.moveTab(1)
        case kVK_Return, kVK_ANSI_KeypadEnter, kVK_Space: model.activate()
        case kVK_Home: model.selectedID = model.entries.first?.id
        case kVK_End: model.selectedID = model.entries.last?.id
        case kVK_Delete, kVK_ForwardDelete:
            if let entry = model.selected, model.isRecent(entry) { model.closeOrRemove(entry) }
        default:
            switch character {
            case "s": model.focusSearch()
            case "p": model.togglePin()
            case "d": model.closeOrRemove()
            case "q" where !flags.contains(.shift): model.quitApp()
            default: return false
            }
        }
        return true
    }
}

if UpdatesInstaller.runHelperIfRequested() { exit(0) }

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
