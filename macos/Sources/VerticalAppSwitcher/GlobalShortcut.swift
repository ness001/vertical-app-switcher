import AppKit
import Carbon

extension Shortcut {
    var combination: (keyCode: UInt32, modifiers: UInt32)? {
        switch self {
        case .controlBacktick: return (UInt32(kVK_ANSI_Grave), UInt32(controlKey))
        case .controlOptionSpace: return (UInt32(kVK_Space), UInt32(controlKey | optionKey))
        case .controlShiftSpace: return (UInt32(kVK_Space), UInt32(controlKey | shiftKey))
        case .commandShiftSpace: return (UInt32(kVK_Space), UInt32(cmdKey | shiftKey))
        case let .custom(keyCode, modifiers): return (keyCode, modifiers)
        case .none: return nil
        }
    }

    var title: String {
        guard let combination else { return "Not set" }
        let flags = combination.modifiers
        return (flags & UInt32(controlKey) != 0 ? "⌃" : "")
            + (flags & UInt32(optionKey) != 0 ? "⌥" : "")
            + (flags & UInt32(shiftKey) != 0 ? "⇧" : "")
            + (flags & UInt32(cmdKey) != 0 ? "⌘" : "")
            + Self.keyTitle(combination.keyCode)
    }

    private static func keyTitle(_ keyCode: UInt32) -> String {
        let special: [UInt32: String] = [
            UInt32(kVK_ANSI_Grave): "`", UInt32(kVK_Space): "Space", UInt32(kVK_Return): "Return", UInt32(kVK_ANSI_KeypadEnter): "Enter",
            UInt32(kVK_Tab): "Tab", UInt32(kVK_Escape): "Esc", UInt32(kVK_Delete): "Delete",
            UInt32(kVK_ForwardDelete): "Forward Delete", UInt32(kVK_LeftArrow): "←", UInt32(kVK_RightArrow): "→",
            UInt32(kVK_UpArrow): "↑", UInt32(kVK_DownArrow): "↓", UInt32(kVK_Home): "Home",
            UInt32(kVK_End): "End", UInt32(kVK_PageUp): "Page Up", UInt32(kVK_PageDown): "Page Down"
        ]
        if let title = special[keyCode] { return title }
        let functionKeys = [kVK_F1, kVK_F2, kVK_F3, kVK_F4, kVK_F5, kVK_F6, kVK_F7, kVK_F8, kVK_F9, kVK_F10,
            kVK_F11, kVK_F12, kVK_F13, kVK_F14, kVK_F15, kVK_F16, kVK_F17, kVK_F18, kVK_F19, kVK_F20]
        if let index = functionKeys.firstIndex(of: Int(keyCode)) { return "F\(index + 1)" }
        let source = TISCopyCurrentKeyboardLayoutInputSource().takeRetainedValue()
        if let pointer = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) {
            let data = Unmanaged<CFData>.fromOpaque(pointer).takeUnretainedValue()
            let layout = UnsafeRawPointer(CFDataGetBytePtr(data)).assumingMemoryBound(to: UCKeyboardLayout.self)
            var deadKey: UInt32 = 0
            var length = 0
            var characters = [UniChar](repeating: 0, count: 8)
            let status = UCKeyTranslate(layout, UInt16(keyCode), UInt16(kUCKeyActionDisplay), 0,
                UInt32(LMGetKbdType()), OptionBits(kUCKeyTranslateNoDeadKeysMask), &deadKey,
                characters.count, &length, &characters)
            if status == noErr && length > 0 {
                let title = String(utf16CodeUnits: characters, count: length).uppercased()
                if title.unicodeScalars.allSatisfy({ !CharacterSet.controlCharacters.contains($0) }) { return title }
            }
        }
        return "Key \(keyCode)"
    }

    static func recorded(_ event: NSEvent) -> Shortcut {
        let flags = event.modifierFlags
        var modifiers: UInt32 = 0
        if flags.contains(.control) { modifiers |= UInt32(controlKey) }
        if flags.contains(.option) { modifiers |= UInt32(optionKey) }
        if flags.contains(.shift) { modifiers |= UInt32(shiftKey) }
        if flags.contains(.command) { modifiers |= UInt32(cmdKey) }
        let keyCode = UInt32(event.keyCode)
        return allCases.first { $0.combination?.keyCode == keyCode && $0.combination?.modifiers == modifiers }
            ?? .custom(keyCode: keyCode, modifiers: modifiers)
    }
}

final class GlobalShortcut {
    private var reference: EventHotKeyRef?
    private var handler: EventHandlerRef?
    private var registered: Shortcut = .none
    var onPress: (() -> Void)?
    var onRecordCurrent: ((Shortcut) -> Void)?

    init() {
        var event = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, context in
            guard let context, let event else { return OSStatus(eventNotHandledErr) }
            var id = EventHotKeyID()
            guard GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                nil, MemoryLayout<EventHotKeyID>.size, nil, &id) == noErr,
                id.signature == 0x56415357, id.id == 1 else { return OSStatus(eventNotHandledErr) }
            let shortcut = Unmanaged<GlobalShortcut>.fromOpaque(context).takeUnretainedValue()
            if let onRecordCurrent = shortcut.onRecordCurrent { onRecordCurrent(shortcut.registered) }
            else { shortcut.onPress?() }
            return noErr
        }, 1, &event, Unmanaged.passUnretained(self).toOpaque(), &handler)
    }

    static func validationError(_ shortcut: Shortcut) -> String? {
        guard let combination = shortcut.combination else { return nil }
        let key = Int(combination.keyCode)
        let flags = combination.modifiers
        let allowed = UInt32(controlKey | optionKey | shiftKey | cmdKey)
        guard key < 128, flags & ~allowed == 0,
            flags & UInt32(controlKey | optionKey | cmdKey) != 0,
            ![kVK_Command, kVK_RightCommand, kVK_Shift, kVK_RightShift, kVK_Option, kVK_RightOption,
                kVK_Control, kVK_RightControl, kVK_CapsLock, kVK_Function].contains(key) else {
            return "Choose a key with Control, Option, or Command. Modifier keys alone are not shortcuts."
        }
        let command = UInt32(cmdKey)
        let commandShift = UInt32(cmdKey | shiftKey)
        let commandOption = UInt32(cmdKey | optionKey)
        if (flags == command && [kVK_Tab, kVK_Space, kVK_ANSI_Q, kVK_ANSI_W, kVK_ANSI_H, kVK_ANSI_M,
                kVK_ANSI_Grave, kVK_ANSI_Period].contains(key))
            || (flags == commandShift && [kVK_Tab, kVK_ANSI_Grave, kVK_ANSI_Q, kVK_ANSI_3,
                kVK_ANSI_4, kVK_ANSI_5].contains(key))
            || (flags == commandOption && [kVK_Escape, kVK_Space, kVK_ANSI_H, kVK_ANSI_M, kVK_ANSI_D].contains(key))
            || (flags == UInt32(controlKey | cmdKey) && key == kVK_ANSI_Q) {
            return "This shortcut is reserved for a standard macOS action. Choose another combination."
        }
        return nil
    }

    private func systemConflict(_ shortcut: Shortcut) -> String? {
        guard let combination = shortcut.combination else { return nil }
        var hotKeys: Unmanaged<CFArray>?
        guard CopySymbolicHotKeys(&hotKeys) == noErr, let values = hotKeys?.takeRetainedValue() as? [[String: Any]] else {
            return "macOS shortcuts could not be checked. Try again."
        }
        if values.contains(where: {
            ($0[kHISymbolicHotKeyEnabled] as? NSNumber)?.boolValue == true
                && ($0[kHISymbolicHotKeyCode] as? NSNumber)?.uint32Value == combination.keyCode
                && ($0[kHISymbolicHotKeyModifiers] as? NSNumber)?.uint32Value == combination.modifiers
        }) { return "This combination is enabled in macOS Keyboard Shortcuts. Choose another shortcut."
        }
        return nil
    }

    func check(_ shortcut: Shortcut) -> String? {
        if let error = Self.validationError(shortcut) ?? systemConflict(shortcut) { return error }
        guard let combination = shortcut.combination else { return nil }
        if let current = registered.combination, reference != nil,
            current.keyCode == combination.keyCode && current.modifiers == combination.modifiers { return nil }
        var probe: EventHotKeyRef?
        let status = RegisterEventHotKey(combination.keyCode, combination.modifiers,
            EventHotKeyID(signature: 0x56415357, id: 2), GetApplicationEventTarget(), 0, &probe)
        if let probe { UnregisterEventHotKey(probe) }
        return status == noErr ? nil : "This shortcut is unavailable or registered by another app. Choose another combination."
    }

    func register(_ shortcut: Shortcut) -> Bool {
        guard handler != nil, Self.validationError(shortcut) == nil, systemConflict(shortcut) == nil else { return false }
        guard let combination = shortcut.combination else {
            if let reference { UnregisterEventHotKey(reference) }
            reference = nil
            registered = .none
            return true
        }
        if let current = registered.combination, reference != nil,
            current.keyCode == combination.keyCode && current.modifiers == combination.modifiers { return true }
        var next: EventHotKeyRef?
        let status = RegisterEventHotKey(combination.keyCode, combination.modifiers,
            EventHotKeyID(signature: 0x56415357, id: 1), GetApplicationEventTarget(), 0, &next)
        guard status == noErr else { return false }
        if let reference { UnregisterEventHotKey(reference) }
        reference = next
        registered = shortcut
        return true
    }

    deinit {
        if let reference { UnregisterEventHotKey(reference) }
        if let handler { RemoveEventHandler(handler) }
    }
}
