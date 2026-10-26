import AppKit
import Carbon

private final class ShortcutRecordButton: NSButton {
    var isRecording = false
    var onRecord: ((NSEvent) -> Void)?
    var onCancel: (() -> Void)?
    override var acceptsFirstResponder: Bool { true }

    override func keyDown(with event: NSEvent) {
        guard isRecording else { super.keyDown(with: event); return }
        if event.keyCode == UInt16(kVK_Escape),
            event.modifierFlags.intersection([.command, .control, .option, .shift]).isEmpty {
            onCancel?()
        } else if !event.isARepeat { onRecord?(event) }
    }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard isRecording, window?.firstResponder === self else { return super.performKeyEquivalent(with: event) }
        keyDown(with: event)
        return true
    }
}

final class ShortcutSetupController: NSWindowController, NSWindowDelegate {
    private let shortcut: GlobalShortcut
    private let onSave: (Shortcut) -> Bool
    private var candidate: Shortcut
    private let valueLabel = NSTextField(labelWithString: "")
    private let feedback = NSTextField(wrappingLabelWithString: "")
    private let recordButton = ShortcutRecordButton(title: "Record shortcut…", target: nil, action: nil)
    private let presets = NSPopUpButton(frame: .zero, pullsDown: false)
    private var modifierButtons: [NSButton] = []
    private var selectedKey: UInt32 = UInt32(kVK_Space)

    init(shortcut: GlobalShortcut, current: Shortcut, onSave: @escaping (Shortcut) -> Bool) {
        self.shortcut = shortcut
        self.candidate = current
        self.onSave = onSave
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 640, height: 600),
            styleMask: [.titled, .closable], backing: .buffered, defer: false)
        panel.title = "Global Shortcut"
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        super.init(window: panel)
        panel.delegate = self
        buildContents()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func show() {
        window?.center()
        showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    private func buildContents() {
        guard let content = window?.contentView else { return }
        let heading = NSTextField(labelWithString: "Choose a global shortcut")
        heading.font = .boldSystemFont(ofSize: 15)
        let detail = NSTextField(wrappingLabelWithString:
            "Use Control, Option, or Command with a key. Checks include enabled macOS shortcuts and registered app hotkeys; not every app shortcut can be detected.")
        detail.textColor = .secondaryLabelColor
        valueLabel.font = .systemFont(ofSize: 22, weight: .medium)
        valueLabel.setAccessibilityLabel("Selected shortcut")
        presets.addItem(withTitle: "Choose a preset…")
        for value in Shortcut.allCases { presets.addItem(withTitle: value.title) }
        presets.target = self
        presets.action = #selector(selectPreset)
        recordButton.target = self
        recordButton.action = #selector(beginRecording)
        recordButton.onRecord = { [weak self] event in
            self?.stopRecording()
            self?.select(Shortcut.recorded(event))
        }
        recordButton.onCancel = { [weak self] in
            self?.stopRecording()
            self?.feedback.stringValue = "Recording canceled. The selection is unchanged."
            self?.feedback.textColor = .secondaryLabelColor
        }
        let clear = NSButton(title: "Clear", target: self, action: #selector(clearShortcut))
        let check = NSButton(title: "Check availability", target: self, action: #selector(checkAvailability))
        let cancel = NSButton(title: "Cancel", target: self, action: #selector(cancel))
        let save = NSButton(title: "Save", target: self, action: #selector(save))
        save.keyEquivalent = "\r"
        feedback.font = .systemFont(ofSize: 12)
        feedback.setAccessibilityLabel("Shortcut availability")
        let controls = NSStackView(views: [recordButton, clear, check])
        controls.orientation = .horizontal
        controls.spacing = 8
        let spacer = NSView()
        let actions = NSStackView(views: [spacer, cancel, save])
        actions.orientation = .horizontal
        actions.spacing = 8
        let stack = NSStackView(views: [heading, detail, valueLabel, presets, keyboardGuide(), controls, feedback, actions])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 12
        stack.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -24),
            stack.topAnchor.constraint(equalTo: content.topAnchor, constant: 22),
            stack.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -22),
            detail.widthAnchor.constraint(equalTo: stack.widthAnchor),
            feedback.widthAnchor.constraint(equalTo: stack.widthAnchor),
            feedback.heightAnchor.constraint(greaterThanOrEqualToConstant: 32),
            actions.widthAnchor.constraint(equalTo: stack.widthAnchor)
        ])
        select(candidate)
    }

    private func keyboardGuide() -> NSView {
        let guide = NSStackView()
        guide.orientation = .vertical
        guide.alignment = .leading
        guide.spacing = 5
        for keys in [
            [("Q", kVK_ANSI_Q), ("W", kVK_ANSI_W), ("E", kVK_ANSI_E), ("R", kVK_ANSI_R), ("T", kVK_ANSI_T), ("Y", kVK_ANSI_Y), ("U", kVK_ANSI_U), ("I", kVK_ANSI_I), ("O", kVK_ANSI_O), ("P", kVK_ANSI_P)],
            [("A", kVK_ANSI_A), ("S", kVK_ANSI_S), ("D", kVK_ANSI_D), ("F", kVK_ANSI_F), ("G", kVK_ANSI_G), ("H", kVK_ANSI_H), ("J", kVK_ANSI_J), ("K", kVK_ANSI_K), ("L", kVK_ANSI_L)],
            [("Z", kVK_ANSI_Z), ("X", kVK_ANSI_X), ("C", kVK_ANSI_C), ("V", kVK_ANSI_V), ("B", kVK_ANSI_B), ("N", kVK_ANSI_N), ("M", kVK_ANSI_M), ("Space", kVK_Space), ("`", kVK_ANSI_Grave)]
        ] {
            let row = NSStackView()
            row.spacing = 4
            for (title, code) in keys {
                let button = NSButton(title: title, target: self, action: #selector(selectGuideKey(_:)))
                button.tag = code
                button.widthAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true
                row.addArrangedSubview(button)
            }
            guide.addArrangedSubview(row)
        }
        let modifiers = NSStackView()
        modifiers.spacing = 8
        for (title, flags) in [("Control ⌃", controlKey), ("Option ⌥", optionKey), ("Shift ⇧", shiftKey), ("Command ⌘", cmdKey)] {
            let button = NSButton(checkboxWithTitle: title, target: self, action: #selector(selectGuideModifiers))
            button.tag = flags
            modifierButtons.append(button)
            modifiers.addArrangedSubview(button)
        }
        guide.addArrangedSubview(modifiers)
        let hint = NSTextField(labelWithString: "US-layout guide · choose modifiers and a key, or record any supported key.")
        hint.font = .systemFont(ofSize: 11)
        hint.textColor = .secondaryLabelColor
        guide.addArrangedSubview(hint)
        return guide
    }

    @objc private func selectGuideKey(_ button: NSButton) {
        selectedKey = UInt32(button.tag)
        selectGuideModifiers()
    }

    @objc private func selectGuideModifiers() {
        stopRecording()
        let flags = modifierButtons.filter { $0.state == .on }.reduce(UInt32(0)) { $0 | UInt32($1.tag) }
        select(.custom(keyCode: selectedKey, modifiers: flags))
    }

    private func select(_ value: Shortcut) {
        candidate = value
        if let combination = value.combination {
            selectedKey = combination.keyCode
            for button in modifierButtons {
                button.state = combination.modifiers & UInt32(button.tag) != 0 ? .on : .off
            }
        } else {
            for button in modifierButtons { button.state = .off }
        }
        valueLabel.stringValue = value.title
        presets.selectItem(at: Shortcut.allCases.firstIndex(of: value).map { $0 + 1 } ?? 0)
        showCheck()
    }

    private func showCheck() {
        if let error = shortcut.check(candidate) {
            feedback.stringValue = error
            feedback.textColor = .systemRed
        } else {
            feedback.stringValue = candidate == .none
                ? "Saving will disable the shortcut. The menu-bar icon remains available."
                : "Available now. Availability will be checked again when saving."
            feedback.textColor = .secondaryLabelColor
        }
    }

    func windowWillClose(_ notification: Notification) { stopRecording() }
    func windowDidResignKey(_ notification: Notification) { stopRecording() }

    private func stopRecording() {
        shortcut.onRecordCurrent = nil
        recordButton.isRecording = false
        recordButton.title = "Record shortcut…"
    }

    @objc private func beginRecording() {
        recordButton.isRecording = true
        recordButton.title = "Press shortcut…"
        window?.makeFirstResponder(recordButton)
        shortcut.onRecordCurrent = { [weak self] value in
            guard let self else { return }
            self.stopRecording()
            self.select(value)
        }
        feedback.stringValue = "Press the shortcut now. Press Escape to cancel recording."
        feedback.textColor = .secondaryLabelColor
    }

    @objc private func selectPreset() {
        stopRecording()
        let index = presets.indexOfSelectedItem - 1
        guard Shortcut.allCases.indices.contains(index) else { return }
        select(Shortcut.allCases[index])
    }

    @objc private func clearShortcut() { stopRecording(); select(.none) }
    @objc private func checkAvailability() { stopRecording(); showCheck() }
    @objc private func cancel() { close() }

    @objc private func save() {
        stopRecording()
        if let error = shortcut.check(candidate) {
            feedback.stringValue = error
            feedback.textColor = .systemRed
            return
        }
        guard onSave(candidate) else {
            feedback.stringValue = "The shortcut could not be saved. The previous shortcut is unchanged."
            feedback.textColor = .systemRed
            return
        }
        close()
    }
}
