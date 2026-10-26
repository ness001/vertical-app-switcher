import AppKit
import SwiftUI

struct SwitcherView: View {
    @ObservedObject var model: SwitcherModel
    @FocusState private var searchFocused: Bool
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var restoringScroll = false

    private var appearance: SwitcherAppearance { model.state.appearance }
    private var accent: Color { appearance.accent }

    var body: some View {
        VStack(spacing: 0) {
            header
            tabs.padding(.horizontal, 22).padding(.bottom, 18)
            if !model.trusted { permissionCard }
            if model.searchVisible { search.padding(.horizontal, 22).padding(.bottom, 14) }
            if let message = model.message {
                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: "exclamationmark.circle")
                    Text(message).font(.system(size: 12)).fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 0)
                    Button { model.message = nil } label: { Image(systemName: "xmark") }
                        .buttonStyle(.plain).accessibilityLabel("Dismiss message")
                }.foregroundStyle(.secondary).padding(12).background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 12))
                    .padding(.horizontal, 22).padding(.bottom, 12)
            }
            list
            footer
        }
        .frame(width: 520, height: model.overlayHeight)
        .background(appearance.backgroundTint)
        .background(VisualEffect().ignoresSafeArea())
        .tint(accent)
        .preferredColorScheme(appearance.preferredColorScheme)
        .overlay(RoundedRectangle(cornerRadius: 24).strokeBorder(.white.opacity(0.2), lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 24))
        .onChange(of: model.query) { _ in model.reconcileSelection() }
        .onChange(of: model.searchFocused) { searchFocused = $0 }
        .onChange(of: searchFocused) { model.searchFocused = $0 }
    }

    private var header: some View {
        HStack(spacing: 12) {
            Image(systemName: "rectangle.stack.fill")
                .font(.system(size: 20, weight: .medium)).foregroundStyle(accent)
                .frame(width: 44, height: 44)
                .background(accent.opacity(0.12), in: RoundedRectangle(cornerRadius: 14))
            VStack(alignment: .leading, spacing: 3) {
                Text("Vertical App Switcher").font(.system(size: 18, weight: .semibold))
            }
            Spacer(minLength: 0)
        }.padding(.horizontal, 22).padding(.top, 25).padding(.bottom, 24)
    }

    private var tabs: some View {
        HStack(spacing: 4) {
            ForEach(SwitcherModel.Tab.allCases, id: \.self) { tab in
                Button {
                    withAnimation(reduceMotion ? nil : .easeOut(duration: 0.15)) { model.changeTab(tab) }
                } label: {
                    HStack(spacing: 6) {
                        VStack(spacing: 5) {
                            Image(systemName: icon(for: tab)).font(.system(size: 12))
                            Text(tab.rawValue).font(.system(size: 10, weight: .semibold))
                        }
                        Text("\(model.entries(in: tab).count)").font(.system(size: 9, design: .monospaced)).foregroundStyle(.secondary)
                    }.frame(maxWidth: .infinity).padding(.vertical, 10)
                        .foregroundStyle(model.tab == tab ? Color.primary : Color.secondary)
                        .background {
                            if model.tab == tab {
                                RoundedRectangle(cornerRadius: 10).fill(.background.opacity(0.8)).shadow(color: .black.opacity(0.07), radius: 3, y: 1)
                            }
                        }
                }.buttonStyle(.plain)
                    .accessibilityAddTraits(model.tab == tab ? [.isSelected] : [])
            }
        }.padding(4).background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 14))
    }

    private var search: some View {
        HStack(spacing: 10) {
            Image(systemName: "magnifyingglass").foregroundStyle(.tertiary)
            TextField("Find a window or app", text: $model.query)
                .textFieldStyle(.plain).font(.system(size: 13))
                .focused($searchFocused).onSubmit { model.activate() }
                .onAppear {
                    DispatchQueue.main.async { searchFocused = model.searchFocused }
                }
                .accessibilityLabel("Search current tab")
            if model.query.isEmpty {
                Text("⌘F").font(.system(size: 11)).foregroundStyle(.tertiary)
            } else {
                Button { model.query = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.tertiary) }
                    .buttonStyle(.plain).accessibilityLabel("Clear search")
            }
        }.padding(12).background(.background.opacity(0.5), in: RoundedRectangle(cornerRadius: 11))
            .overlay(RoundedRectangle(cornerRadius: 11).strokeBorder(searchFocused ? accent.opacity(0.65) : Color.primary.opacity(0.07), lineWidth: 1))
    }

    private var list: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(sectionTitle).font(.system(size: 10, weight: .semibold)).tracking(1.3)
                Spacer()
                Text("\(model.entries.count)").font(.system(size: 11, weight: .medium, design: .monospaced))
            }.foregroundStyle(.tertiary).padding(.horizontal, 28)
            if model.entries.isEmpty {
                Spacer()
                VStack(spacing: 12) {
                    Image(systemName: model.query.isEmpty ? icon(for: model.tab) : "magnifyingglass")
                        .font(.system(size: 30, weight: .light)).foregroundStyle(accent.opacity(0.7))
                    Text(emptyTitle).font(.system(size: 16, weight: .medium))
                    Text(emptyDetail).font(.system(size: 12)).foregroundStyle(.secondary)
                        .multilineTextAlignment(.center).frame(maxWidth: 280)
                }.frame(maxWidth: .infinity)
                Spacer()
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(spacing: 6) {
                            ForEach(Array(model.entries.enumerated()), id: \.element.id) { index, entry in
                                row(entry, index: index).id(entry.id)
                            }
                        }.padding(.horizontal, 14).padding(.vertical, 4)
                    }
                    .background(ScrollPositionKeeper(tab: model.tab, model: model))
                    .onChange(of: model.selectedID) { id in
                        guard !restoringScroll, !model.restoringTab else { return }
                        if let id { proxy.scrollTo(id, anchor: .center) }
                    }
                    .onChange(of: model.tab) { _ in
                        restoringScroll = true
                        DispatchQueue.main.async { restoringScroll = false }
                    }
                }
            }
        }.frame(maxHeight: .infinity)
    }

    private func row(_ entry: WindowEntry, index: Int) -> some View {
        let selected = model.selectedID == entry.id && !model.searchFocused
        return Button { model.selectedID = entry.id; model.activate(entry) } label: {
            HStack(spacing: 13) {
                Image(nsImage: NSWorkspace.shared.icon(forFile: entry.app.path))
                    .resizable().interpolation(.high).frame(width: 38, height: 38)
                VStack(alignment: .leading, spacing: 5) {
                    Text(entry.title).font(.system(size: 13, weight: .medium)).lineLimit(1)
                    Text(entry.window == nil && entry.pid == nil && !entry.id.hasPrefix("demo") ? "Launch \(entry.app.name)" : entry.subtitle)
                        .font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer(minLength: 4)
                if model.isPinned(entry) {
                    Image(systemName: "pin.fill").font(.system(size: 10)).foregroundStyle(accent)
                }
                if index < 9 {
                    Text("⌘\(index + 1)").font(.system(size: 10, weight: .medium, design: .monospaced))
                        .foregroundStyle(selected ? accent : Color.secondary.opacity(0.65))
                }
            }.padding(.horizontal, 14).padding(.vertical, 13)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(selected ? appearance.selectionFill : .clear, in: RoundedRectangle(cornerRadius: 13))
                .overlay(RoundedRectangle(cornerRadius: 13).strokeBorder(selected ? appearance.selectionStroke : .clear, lineWidth: 1))
                .contentShape(RoundedRectangle(cornerRadius: 13))
        }.buttonStyle(.plain)
            .accessibilityLabel("\(entry.title), \(entry.subtitle)")
            .accessibilityAddTraits(selected ? [.isSelected] : [])
            .contextMenu {
                Button(entry.window == nil ? "Open app" : "Switch to window") { model.activate(entry) }
                Button(model.isPinned(entry) ? "Unpin app" : "Pin app") { model.togglePin(entry) }
                if entry.window != nil { Button("Close window") { model.closeOrRemove(entry) } }
                if entry.pid != nil {
                    Divider()
                    Button("Force quit app…", role: .destructive) { model.forceStop(entry) }
                }
                if model.isRecent(entry) { Button("Remove from Recent") { model.closeOrRemove(entry) } }
            }
    }

    private var permissionCard: some View {
        VStack(alignment: .leading, spacing: 9) {
            Label("Allow window access", systemImage: "hand.raised.fill").font(.system(size: 13, weight: .semibold))
            Text("Accessibility access lets the switcher find, focus, and close windows. Window titles stay on this Mac. Screen Recording is not needed.")
                .font(.system(size: 12)).foregroundStyle(.secondary)
            HStack {
                Button("Enable Accessibility") {
                    guard !model.isDemo else { return }
                    WindowCatalog.requestAccess(); openAccessibilitySettings()
                }
                Button("Check again") { model.refresh() }
            }.controlSize(.small)
        }.padding(14).frame(maxWidth: .infinity, alignment: .leading)
            .background(accent.opacity(0.09), in: RoundedRectangle(cornerRadius: 13))
            .padding(.horizontal, 22).padding(.bottom, 14)
    }

    private var footer: some View {
        HStack(spacing: 12) {
            Text("↑↓ Navigate")
            Text("↵ Open")
            Text("P Pin")
            Text("D Close")
            Text("Q Quit")
            Text("S Search")
            Spacer(minLength: 0)
            Text("esc Dismiss")
        }.font(.system(size: 10, weight: .medium)).foregroundStyle(.secondary)
            .padding(.horizontal, 26).padding(.vertical, 17)
            .frame(maxWidth: .infinity).background(.quaternary.opacity(0.3))
    }

    private var sectionTitle: String {
        switch model.tab {
        case .open: return "\(model.query.isEmpty ? "YOUR WINDOWS" : "MATCHING WINDOWS")"
        case .pinned: return "PINNED APPS"
        case .recent: return "RECENTLY CLOSED"
        case .tray: return "MENU-BAR APPS"
        case .desktops: return "OTHER DESKTOPS"
        }
    }

    private var emptyTitle: String {
        if !model.query.isEmpty { return "No matching apps" }
        switch model.tab {
        case .open: return model.trusted ? "A clear desktop" : "Your windows, one shortcut away"
        case .pinned: return "Keep your favorites close"
        case .recent: return "Start something worth returning to"
        case .tray: return "No menu-bar apps"
        case .desktops: return "Everything is on this desktop"
        }
    }

    private var emptyDetail: String {
        if !model.query.isEmpty { return "Try another name, or clear your search." }
        switch model.tab {
        case .open: return model.trusted ? "Open an app window and it will appear here." : "Enable Accessibility above to get started."
        case .pinned: return "Press P on a window or choose Pin app from its menu."
        case .recent: return "Closed windows appear here. Available document or folder locations reopen directly; otherwise the app is launched."
        case .tray: return "Detected menu-bar apps without open windows appear here, including accessible hidden items."
        case .desktops: return "Windows detected on other Spaces appear here. If macOS changes its private interfaces, unidentified windows remain in Open."
        }
    }

    private func icon(for tab: SwitcherModel.Tab) -> String {
        switch tab {
        case .open: return "square.on.square"
        case .pinned: return "pin"
        case .recent: return "clock.arrow.circlepath"
        case .tray: return "menubar.rectangle"
        case .desktops: return "rectangle.3.group"
        }
    }
}

private struct ScrollPositionKeeper: NSViewRepresentable {
    let tab: SwitcherModel.Tab
    let model: SwitcherModel

    final class Probe: NSView {
        var tab: SwitcherModel.Tab?
        weak var model: SwitcherModel?
        weak var scroll: NSScrollView?
        var token: NSObjectProtocol?

        func bind(_ next: SwitcherModel.Tab, model: SwitcherModel) {
            self.model = model
            func findScroll(in view: NSView) -> NSScrollView? {
                if let scroll = view as? NSScrollView { return scroll }
                for child in view.subviews {
                    if let scroll = findScroll(in: child) { return scroll }
                }
                return nil
            }
            var ancestor = superview
            var found = enclosingScrollView
            while found == nil, let view = ancestor {
                found = findScroll(in: view)
                ancestor = view.superview
            }
            guard let scroll = found else { return }
            if self.scroll !== scroll {
                if let token { NotificationCenter.default.removeObserver(token) }
                self.scroll = scroll
                scroll.contentView.postsBoundsChangedNotifications = true
                token = NotificationCenter.default.addObserver(forName: NSView.boundsDidChangeNotification,
                    object: scroll.contentView, queue: .main) { [weak self] _ in
                    guard let self, let tab = self.tab else { return }
                    self.model?.scrollOffsets[tab] = self.scroll?.contentView.bounds.origin.y ?? 0
                }
            }
            guard tab != next else { return }
            if let tab { model.scrollOffsets[tab] = scroll.contentView.bounds.origin.y }
            tab = next
            let offset = model.scrollOffsets[next] ?? 0
            DispatchQueue.main.async { [weak self] in
                guard let self, self.tab == next, let scroll = self.scroll else { return }
                let maximum = max(0, (scroll.documentView?.frame.height ?? 0) - scroll.contentView.bounds.height)
                scroll.contentView.scroll(to: NSPoint(x: 0, y: min(offset, maximum)))
                scroll.reflectScrolledClipView(scroll.contentView)
            }
        }
        deinit { if let token { NotificationCenter.default.removeObserver(token) } }
    }

    func makeNSView(context: Context) -> Probe { Probe() }
    func updateNSView(_ view: Probe, context: Context) {
        DispatchQueue.main.async { view.bind(tab, model: model) }
    }
}

private struct VisualEffect: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .hudWindow
        view.blendingMode = .behindWindow
        view.state = .active
        return view
    }
    func updateNSView(_ nsView: NSVisualEffectView, context: Context) {}
}

func openAccessibilitySettings() {
    if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
        NSWorkspace.shared.open(url)
    }
}

extension Notification.Name {
    static let changeShortcut = Notification.Name("VerticalAppSwitcher.changeShortcut")
    static let setupShortcut = Notification.Name("VerticalAppSwitcher.setupShortcut")
}
