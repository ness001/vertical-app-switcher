import AppKit
import Combine

final class SwitcherModel: ObservableObject {
    enum Tab: String, CaseIterable {
        case open = "Open", pinned = "Pinned", recent = "Recent", tray = "Tray", desktops = "Desktops"
    }

    @Published var tab: Tab = .open
    @Published var query = ""
    @Published var selectedID: String?
    @Published var searchVisible = false
    @Published var searchFocused = false
    @Published var overlayHeight: CGFloat = 680
    @Published var overlayVisible = false
    @Published var confirmingAction = false
    @Published var backgroundApps: [WindowEntry] = []
    @Published var desktopWindows: [WindowEntry] = []
    @Published var windows: [WindowEntry] = []
    @Published var state = SavedState()
    @Published var trusted = WindowCatalog.isTrusted
    @Published var message: String?
    @Published var scanning = false
    private let store: StateStore
    private let catalog = WindowCatalog()
    private let worker = DispatchQueue(label: "app.verticalswitcher.catalog", qos: .userInitiated)
    private var timer: Timer?
    private var observerTimer: Timer?
    private var previousRunning: [SavedApp]?
    private var ranks: [String: Int] = [:]
    private var sequence = 0
    private var frontPID: pid_t?
    private var pendingOpen = false
    private var selections: [Tab: String] = [:]
    private var pendingCloses: [String: Date] = [:]
    var scrollOffsets: [Tab: CGFloat] = [:]
    private(set) var restoringTab = false
    private let demo: Bool
    var isDemo: Bool { demo }
    private var canPersist = true
    var dismiss: (() -> Void)?

    init(store: StateStore = .standard, demo: Bool = false) {
        self.store = store
        self.demo = demo
        do { state = try store.load() }
        catch {
            canPersist = false
            message = "Saved settings could not be read: \(error.localizedDescription). The existing file will not be overwritten."
        }
        if demo {
            trusted = true
            let samples = [("Safari", "A little room for inspiration", "/Applications/Safari.app"),
                           ("Notes", "Ideas for the week", "/System/Applications/Notes.app"),
                           ("Finder", "Projects", "/System/Library/CoreServices/Finder.app"),
                           ("Calendar", "Today", "/System/Applications/Calendar.app")]
            windows = samples.enumerated().map { index, item in
                WindowEntry(id: "demo-\(index)", app: SavedApp(path: item.2, name: item.0), pid: nil,
                            title: item.1, window: nil, minimized: index == 2, hidden: false)
            }
            state = SavedState(pins: [windows[0].app, windows[2].app])
            selectedID = windows.first?.id
        }
    }

    func entries(in tab: Tab) -> [WindowEntry] {
        let base: [WindowEntry]
        switch tab {
        case .open: base = windows
        case .tray: base = backgroundApps
        case .desktops: base = desktopWindows
        case .pinned:
            base = state.pins.flatMap { app -> [WindowEntry] in
                let live = (windows + backgroundApps + desktopWindows).filter { $0.app.path == app.path }
                let recent = state.closedWindows.filter { identity in
                    guard identity.app.path == app.path else { return false }
                    if let url = identity.documentURL {
                        return !live.contains { $0.documentURL == url }
                    }
                    return live.isEmpty
                }.map(WindowEntry.recent)
                let represented = live + recent
                return represented.isEmpty ? [.saved(app)] : represented
            }
        case .recent:
            base = state.closedWindows.filter { identity in
                if let url = identity.documentURL {
                    return !(windows + desktopWindows).contains { $0.app.path == identity.app.path && $0.documentURL == url }
                }
                return !(windows + desktopWindows + backgroundApps).contains { $0.app.path == identity.app.path }
            }.map(WindowEntry.recent)
        }
        let term = query.trimmingCharacters(in: .whitespacesAndNewlines)
        return term.isEmpty ? base : base.filter {
            $0.title.localizedCaseInsensitiveContains(term) || $0.app.name.localizedCaseInsensitiveContains(term)
        }
    }

    var entries: [WindowEntry] { entries(in: tab) }
    var selected: WindowEntry? { entries.first { $0.id == selectedID } }

    func isRecent(_ entry: WindowEntry) -> Bool {
        entry.window == nil && state.closedWindows.contains {
            $0.id == ClosedWindowIdentity(app: entry.app, title: entry.title, documentURL: entry.documentURL).id
        }
    }

    func focusSearch() { searchVisible = true; searchFocused = true }
    func isPinned(_ entry: WindowEntry) -> Bool { state.pins.contains { $0.path == entry.app.path } }

    func start() {
        guard !demo else { return }
        catalog.observeFocusChanges { [weak self] _ in self?.refresh() }
        refresh()
        timer = Timer.scheduledTimer(withTimeInterval: 0.4, repeats: true) { [weak self] _ in self?.refresh() }
        observerTimer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
            self?.catalog.synchronizeFocusObservers()
        }
    }

    func prepareToOpen() {
        query = ""
        searchVisible = false
        searchFocused = false
        frontPID = NSWorkspace.shared.frontmostApplication?.processIdentifier
        tab = .open
        pendingOpen = true
        if demo { reconcileSelection(); return }
        refresh()
    }

    func refresh() {
        guard !scanning, !demo else { return }
        scanning = true
        let pid = NSWorkspace.shared.frontmostApplication?.processIdentifier
        worker.async { [weak self] in
            guard let self else { return }
            let snapshot = self.catalog.snapshot()
            let focused = pid.flatMap { self.catalog.focusedWindowID(pid: $0) }
            DispatchQueue.main.async { self.apply(snapshot, focusedID: focused, scannedPID: pid) }
        }
    }

    private func apply(_ snapshot: CatalogSnapshot, focusedID: String?, scannedPID: pid_t?) {
        scanning = false
        let oldIndex = entries.firstIndex { $0.id == selectedID } ?? 0
        trusted = WindowCatalog.isTrusted
        if let focusedID { sequence += 1; ranks[focusedID] = sequence }
        if let previousRunning {
            let closed = previousRunning.filter { app in
                !snapshot.running.contains(where: { $0.path == app.path })
            }
            for app in closed where !snapshot.closedWindows.contains(where: { $0.app.path == app.path }) { state.recordClosed(app) }
            for identity in snapshot.closedWindows { state.recordClosedWindow(identity) }
            if !closed.isEmpty || !snapshot.closedWindows.isEmpty {
                if !demo { DiagnosticLog.shared.write("Recent history updated: \(closed.count) exited apps, \(snapshot.closedWindows.count) closed identities") }
                persist()
            }
        }
        previousRunning = snapshot.running
        for (id, requested) in pendingCloses {
            if !snapshot.windows.contains(where: { $0.id == id }) {
                pendingCloses.removeValue(forKey: id)
            } else if Date().timeIntervalSince(requested) >= 3 {
                message = "The window is still open. The app may need confirmation or may not support closing through Accessibility. Open it and close it there."
                pendingCloses.removeValue(forKey: id)
            }
        }
        backgroundApps = snapshot.windows.filter(\.appOnly)
        desktopWindows = snapshot.windows.filter { !$0.appOnly && $0.otherDesktop }
        windows = snapshot.windows.filter { !$0.appOnly && !$0.otherDesktop }.enumerated().sorted {
            let left = ranks[$0.element.id] ?? 0
            let right = ranks[$1.element.id] ?? 0
            return left == right ? $0.offset < $1.offset : left > right
        }.map(\.element)
        let ids = Set((windows + desktopWindows).map(\.id))
        ranks = ranks.filter { ids.contains($0.key) }
        if pendingOpen {
            pendingOpen = false
            selectedID = Self.openingSelection(in: windows, focusedID: focusedID,
                                               scannedPID: scannedPID, frontPID: frontPID)
        }
        reconcileSelection(preferredIndex: oldIndex)
    }

    static func openingSelection(in windows: [WindowEntry], focusedID: String?,
                                 scannedPID: pid_t?, frontPID: pid_t?) -> String? {
        // A scan already in flight may describe a different foreground app.
        let matchingFocus = scannedPID == frontPID ? focusedID : nil
        let foregroundIndex = windows.firstIndex { $0.id == matchingFocus }
            ?? windows.firstIndex { $0.pid == frontPID }
        guard let index = foregroundIndex else { return windows.first?.id }
        return windows[(index + 1) % windows.count].id
    }

    func changeTab(_ next: Tab) {
        pendingOpen = false
        restoringTab = true
        DispatchQueue.main.async { [weak self] in self?.restoringTab = false }
        if let selectedID { selections[tab] = selectedID }
        tab = next
        selectedID = selections[next]
        searchFocused = false
        reconcileSelection()
    }

    func moveTab(_ delta: Int) {
        let all = Tab.allCases
        let index = all.firstIndex(of: tab) ?? 0
        changeTab(all[(index + delta + all.count) % all.count])
    }

    func reconcileSelection(preferredIndex: Int = 0) {
        if !entries.contains(where: { $0.id == selectedID }) {
            selectedID = entries.isEmpty ? nil : entries[min(max(preferredIndex, 0), entries.count - 1)].id
        }
    }

    func move(_ delta: Int) {
        guard !entries.isEmpty else { return }
        if searchFocused { searchFocused = false; reconcileSelection(); return }
        let index = entries.firstIndex { $0.id == selectedID } ?? 0
        selectedID = entries[(index + delta + entries.count) % entries.count].id
    }

    func activate(_ entry: WindowEntry? = nil) {
        guard !demo, let entry = entry ?? selected else { return }
        dismiss?()
        catalog.activate(entry) { [weak self] error in
            if let error { self?.showActionError(error) }
        }
    }

    func activateNumber(_ number: Int) {
        guard entries.indices.contains(number) else { return }
        activate(entries[number])
    }

    func togglePin(_ entry: WindowEntry? = nil) {
        guard let entry = entry ?? selected else { return }
        state.togglePin(entry.app)
        persist()
        reconcileSelection()
    }

    func closeOrRemove(_ entry: WindowEntry? = nil) {
        guard !demo, let entry = entry ?? selected else { return }
        if entry.window != nil {
            if let error = catalog.close(entry) {
                DiagnosticLog.shared.write("Window close failed: \(error)")
                message = error
            } else {
                pendingCloses[entry.id] = Date()
                message = "Close requested for \(entry.app.name). If the app asks for confirmation, finish closing in that app."
            }
            refresh()
        } else if state.closedWindows.contains(where: {
            $0.id == ClosedWindowIdentity(app: entry.app, title: entry.title, documentURL: entry.documentURL).id
        }) {
            let identity = ClosedWindowIdentity(app: entry.app, title: entry.title, documentURL: entry.documentURL)
            state.closedWindows.removeAll { $0.id == identity.id }
            state.recent.removeAll { $0.path == entry.app.path }
            persist()
            reconcileSelection()
        } else {
            message = "This app has no window to close. Open it first, or use its own Quit command."
        }
    }

    func quitApp(_ entry: WindowEntry? = nil) {
        guard !demo, !searchFocused, !confirmingAction, let entry = entry ?? selected else { return }
        guard let pid = entry.pid, pid != ProcessInfo.processInfo.processIdentifier,
              let app = NSRunningApplication(processIdentifier: pid),
              app.bundleURL?.path == entry.app.path, !app.isTerminated else {
            message = "This app is not running or can no longer be identified. Select a running app to quit."
            return
        }
        if app.terminate() {
            message = "Quit requested for \(entry.app.name). If it asks to save changes or confirm, finish quitting in that app."
        } else {
            message = "macOS did not allow \(entry.app.name) to quit. Open it and use its own Quit command."
        }
        refresh()
    }

    func forceStop(_ entry: WindowEntry) {
        guard !demo, !confirmingAction, let pid = entry.pid,
              let app = NSRunningApplication(processIdentifier: pid),
              app.bundleURL?.path == entry.app.path, !app.isTerminated else { return }
        confirmingAction = true
        defer { confirmingAction = false }
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Force quit \(entry.app.name)?"
        alert.informativeText = "Unsaved work in all of this app’s windows may be lost. A normal close is safer."
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Force Quit")
        guard alert.runModal() == .alertSecondButtonReturn else { return }
        guard app.bundleURL?.path == entry.app.path, !app.isTerminated else { return }
        if !app.forceTerminate() { message = "macOS did not allow this app to be force quit." }
        DiagnosticLog.shared.write("User-confirmed force quit requested for process \(pid)")
        refresh()
    }

    @discardableResult
    func setShortcut(_ shortcut: Shortcut) -> Bool {
        guard canPersist || demo else { return false }
        var next = state
        next.shortcut = shortcut
        do {
            if !demo { try store.save(next) }
            state = next
            return true
        } catch {
            DiagnosticLog.shared.write("Shortcut persistence failed: \(error.localizedDescription)")
            message = "The shortcut could not be saved: \(error.localizedDescription)"
            return false
        }
    }

    @discardableResult
    func setAppearance(_ appearance: SwitcherAppearance) -> Bool {
        guard canPersist || demo else { return false }
        var next = state
        next.appearance = appearance
        do {
            if !demo { try store.save(next) }
            state = next
            return true
        } catch {
            DiagnosticLog.shared.write("Appearance persistence failed: \(error.localizedDescription)")
            message = "The appearance could not be saved: \(error.localizedDescription)"
            return false
        }
    }

    private func persist() {
        guard !demo, canPersist else { return }
        do { try store.save(state) }
        catch {
            DiagnosticLog.shared.write("State persistence failed: \(error.localizedDescription)")
            message = "Settings could not be saved: \(error.localizedDescription)"
        }
    }

    private func showActionError(_ text: String) {
        if !demo { DiagnosticLog.shared.write("Window activation failed: \(text)") }
        let alert = NSAlert()
        alert.messageText = "Unable to switch windows"
        alert.informativeText = text
        alert.runModal()
    }
}
