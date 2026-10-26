import AppKit
import ApplicationServices

final class WindowFocusObserver {
    private var observers: [pid_t: AXObserver] = [:]
    private var tokens: [NSObjectProtocol] = []
    private var onChange: ((pid_t) -> Void)?

    func start(_ onChange: @escaping (pid_t) -> Void) {
        precondition(Thread.isMainThread)
        self.onChange = onChange
        guard tokens.isEmpty else { synchronize(); return }
        let center = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didLaunchApplicationNotification, NSWorkspace.didTerminateApplicationNotification,
                     NSWorkspace.didActivateApplicationNotification] {
            tokens.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] notification in
                self?.synchronize()
                if let app = notification.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication {
                    self?.onChange?(app.processIdentifier)
                }
            })
        }
        synchronize()
    }

    func synchronize() {
        let applications = NSWorkspace.shared.runningApplications.filter {
            $0.processIdentifier != ProcessInfo.processInfo.processIdentifier && $0.activationPolicy != .prohibited && !$0.isTerminated
        }
        let pids = Set(applications.map(\.processIdentifier))
        for pid in Array(observers.keys) where !pids.contains(pid) {
            if let observer = observers.removeValue(forKey: pid) {
                CFRunLoopRemoveSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(observer), .commonModes)
            }
        }
        guard AXIsProcessTrusted() else { return }
        for app in applications where observers[app.processIdentifier] == nil {
            var observer: AXObserver?
            let result = AXObserverCreate(app.processIdentifier, { _, element, _, context in
                guard let context else { return }
                var pid: pid_t = 0
                guard AXUIElementGetPid(element, &pid) == .success else { return }
                Unmanaged<WindowFocusObserver>.fromOpaque(context).takeUnretainedValue().onChange?(pid)
            }, &observer)
            guard result == .success, let observer else { continue }
            let element = AXUIElementCreateApplication(app.processIdentifier)
            AXUIElementSetMessagingTimeout(element, 0.15)
            let context = Unmanaged.passUnretained(self).toOpaque()
            var subscribed = false
            for name in [kAXFocusedWindowChangedNotification, kAXMainWindowChangedNotification] {
                let result = AXObserverAddNotification(observer, element, name as CFString, context)
                if result == .success || result == .notificationAlreadyRegistered { subscribed = true }
            }
            guard subscribed else { continue }
            observers[app.processIdentifier] = observer
            CFRunLoopAddSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(observer), .commonModes)
        }
    }

    deinit {
        for token in tokens { NSWorkspace.shared.notificationCenter.removeObserver(token) }
        for observer in observers.values {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(observer), .commonModes)
        }
    }
}
