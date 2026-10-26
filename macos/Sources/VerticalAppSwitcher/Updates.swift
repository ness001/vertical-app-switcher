import AppKit
import CryptoKit
import Foundation

struct UpdateVersion: Comparable, CustomStringConvertible, Equatable {
    let components: [Int]
    var description: String { components.map(String.init).joined(separator: ".") }

    init(_ text: String) throws {
        let parts = text.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3, parts.allSatisfy({ part in
            !part.isEmpty && part.allSatisfy { $0.isASCII && $0.isNumber }
                && (part.count == 1 || part.first != "0")
                && Int(part).map { (0...65534).contains($0) } == true
        }) else { throw UpdateError.invalid("The release version is not supported.") }
        components = parts.map { Int($0)! }
    }

    static func < (lhs: Self, rhs: Self) -> Bool {
        lhs.components.lexicographicallyPrecedes(rhs.components)
    }
}

enum UpdateError: LocalizedError {
    case invalid(String)
    var errorDescription: String? { if case .invalid(let message) = self { return message }; return nil }
}

struct UpdateRelease {
    static let repository = "https://github.com/ness001/vertical-app-switcher"
    static let api = URL(string: "https://api.github.com/repos/ness001/vertical-app-switcher/releases/latest")!
    let version: UpdateVersion
    let packageName: String
    let packageURL: URL
    let checksumURL: URL

    static var architecture: String {
        #if arch(arm64)
        return "arm64"
        #else
        return "x64"
        #endif
    }

    static func parse(_ data: Data, installed: String, architecture: String = architecture) throws -> Self? {
        struct Asset: Decodable { let name: String; let browser_download_url: String }
        struct Release: Decodable {
            let draft: Bool
            let prerelease: Bool
            let tag_name: String
            let assets: [Asset]
        }
        let release = try JSONDecoder().decode(Release.self, from: data)
        guard !release.draft, !release.prerelease else { return nil }
        guard release.tag_name.first == "v" else { throw UpdateError.invalid("The release tag must start with v.") }
        let version = try UpdateVersion(String(release.tag_name.dropFirst()))
        guard try isNewerStable(version, than: installed) else { return nil }
        guard ["arm64", "x64"].contains(architecture) else { throw UpdateError.invalid("Unsupported app architecture.") }
        for runtime in ["osx-universal", "osx-\(architecture)"] {
            let name = "VerticalAppSwitcher-\(version)-\(runtime).zip"
            guard release.assets.contains(where: { $0.name == name }) else { continue }
            func asset(_ name: String) throws -> URL {
                let expected = "\(repository)/releases/download/\(release.tag_name)/\(name)"
                let matches = release.assets.filter { $0.name == name }
                guard matches.count == 1, matches[0].browser_download_url == expected,
                      let url = URL(string: expected) else {
                    throw UpdateError.invalid("The release is missing a trusted asset: \(name)")
                }
                return url
            }
            return try Self(version: version, packageName: name, packageURL: asset(name), checksumURL: asset(name + ".sha256"))
        }
        throw UpdateError.invalid("This release has no compatible macOS package.")
    }

    static func installedVersion(in bundle: Bundle) -> String {
        bundle.object(forInfoDictionaryKey: "VerticalAppSwitcherReleaseVersion") as? String
            ?? bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
    }

    static func isNewerStable(_ version: UpdateVersion, than installed: String) throws -> Bool {
        let parts = installed.split(separator: "-", maxSplits: 1, omittingEmptySubsequences: false)
        let base = try UpdateVersion(String(parts[0]))
        let prerelease = parts.count == 2
        if prerelease {
            let identifiers = parts[1].split(separator: ".", omittingEmptySubsequences: false)
            guard identifiers.allSatisfy({ identifier in
                !identifier.isEmpty && identifier.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-") }
                    && (!identifier.allSatisfy(\.isNumber) || identifier.count == 1 || identifier.first != "0")
            }) else { throw UpdateError.invalid("The installed release version is invalid.") }
        }
        return version > base || (version == base && prerelease)
    }

    static func verifyChecksum(_ data: Data, checksum: Data, packageName: String) throws {
        guard let text = String(data: checksum, encoding: .utf8) else { throw UpdateError.invalid("Invalid update checksum.") }
        let fields = text.trimmingCharacters(in: .whitespacesAndNewlines).split(whereSeparator: { $0.isWhitespace })
        guard fields.count == 2, fields[1] == Substring(packageName) || fields[1] == Substring("*" + packageName),
              fields[0].count == 64, fields[0].allSatisfy({ $0.isASCII && $0.isHexDigit }) else {
            throw UpdateError.invalid("The checksum must name the exact update package.")
        }
        let actual = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        guard actual == fields[0].lowercased() else { throw UpdateError.invalid("The update SHA-256 checksum does not match.") }
    }
}

// Each request owns a serial delegate queue, bounds its body, and validates every redirect.
final class UpdateRequest: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let limit: Int
    private let initialURL: URL
    private var body = Data()
    private var continuation: CheckedContinuation<Data?, Error>?
    private var failure: Error?
    private var notFound = false
    private var session: URLSession?

    init(url: URL, limit: Int) { initialURL = url; self.limit = limit }

    static func trusted(_ url: URL, initial: URL) -> Bool {
        guard url.scheme == "https", url.user == nil, url.password == nil, url.port == nil, url.fragment == nil else { return false }
        if url == initial { return true }
        guard initial.host == "github.com" else { return false }
        return ["release-assets.githubusercontent.com", "objects.githubusercontent.com"].contains(url.host ?? "")
            && url.path.hasPrefix("/github-production-release-asset/")
    }

    func fetch() async throws -> Data? {
        try await withCheckedThrowingContinuation { continuation in
            self.continuation = continuation
            let config = URLSessionConfiguration.ephemeral
            config.timeoutIntervalForRequest = 30
            config.timeoutIntervalForResource = 300
            config.httpCookieStorage = nil
            config.urlCredentialStorage = nil
            let session = URLSession(configuration: config, delegate: self, delegateQueue: nil)
            self.session = session
            var request = URLRequest(url: initialURL)
            request.setValue("VerticalAppSwitcher-macOS", forHTTPHeaderField: "User-Agent")
            request.setValue(initialURL.host == "api.github.com" ? "application/vnd.github+json" : "application/octet-stream", forHTTPHeaderField: "Accept")
            session.dataTask(with: request).resume()
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        guard let url = request.url, Self.trusted(url, initial: initialURL) else {
            failure = UpdateError.invalid("An update download redirected outside the trusted HTTPS asset hosts.")
            completionHandler(nil)
            return
        }
        completionHandler(request)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let response = response as? HTTPURLResponse, let url = response.url,
              Self.trusted(url, initial: initialURL) else {
            failure = UpdateError.invalid("Update downloads require a trusted HTTPS URL.")
            completionHandler(.cancel)
            return
        }
        if response.statusCode == 404 && initialURL == UpdateRelease.api {
            notFound = true
            completionHandler(.cancel)
        } else if response.statusCode != 200 || response.expectedContentLength > Int64(limit) {
            failure = UpdateError.invalid("The update server returned an error or an oversized response (HTTP \(response.statusCode)).")
            completionHandler(.cancel)
        } else { completionHandler(.allow) }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard data.count <= limit - body.count else {
            failure = UpdateError.invalid("The update response is too large.")
            dataTask.cancel()
            return
        }
        body.append(data)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if notFound { continuation?.resume(returning: nil) }
        else if let error = failure ?? error { continuation?.resume(throwing: error) }
        else { continuation?.resume(returning: body) }
        continuation = nil
        session.finishTasksAndInvalidate()
        self.session = nil
    }
}

@MainActor
final class UpdatesController: NSObject {
    private let canShow: () -> Bool
    private let requestExit: () -> Void
    private let checkItem = NSMenuItem(title: "Check for Updates…", action: nil, keyEquivalent: "")
    private let automaticItem = NSMenuItem(title: "Automatically Check for Updates", action: nil, keyEquivalent: "")
    private let defaults = UserDefaults.standard
    private var timer: Timer?
    private var busy = false
    private var installing = false
    private var stopped = false
    private var release: UpdateRelease?
    private var workspace: URL?
    private var installPlan: URL?
    private var status = ""
    private var automatic: Bool { defaults.object(forKey: "AutomaticUpdateChecks") as? Bool ?? true }
    private var postponedUntil: Date { defaults.object(forKey: "UpdatePostponedUntil") as? Date ?? .distantPast }
    private var installedVersion: String { UpdateRelease.installedVersion(in: .main) }

    init(menu: NSMenu, canShow: @escaping () -> Bool, requestExit: @escaping () -> Void) {
        self.canShow = canShow
        self.requestExit = requestExit
        super.init()
        checkItem.target = self
        checkItem.action = #selector(checkManually)
        automaticItem.target = self
        automaticItem.action = #selector(toggleAutomatic)
        automaticItem.state = automatic ? .on : .off
        let index = max(0, menu.items.firstIndex(where: { $0.title.hasPrefix("About") }) ?? menu.items.count)
        menu.insertItem(checkItem, at: index)
        menu.insertItem(automaticItem, at: index + 1)
        menu.insertItem(.separator(), at: index + 2)
    }

    func start() {
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 12 * 60 * 60, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.check(manual: false) }
        }
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 20_000_000_000)
            await self?.check(manual: false)
        }
    }

    func applicationWillTerminate() {
        stopped = true
        timer?.invalidate()
        if let installPlan { try? Data("normal-exit".utf8).write(to: installPlan.deletingLastPathComponent().appendingPathComponent("exited"), options: .atomic) }
        else if let workspace { try? FileManager.default.removeItem(at: workspace) }
    }

    @objc private func toggleAutomatic() {
        defaults.set(!automatic, forKey: "AutomaticUpdateChecks")
        automaticItem.state = automatic ? .on : .off
    }

    @objc private func checkManually() {
        guard !busy, !installing, !stopped, canShow() else { return }
        if release != nil, workspace != nil { offerUpdate(); return }
        Task { await check(manual: true) }
    }

    private func check(manual: Bool) async {
        guard !busy, !installing, !stopped, manual || (automatic && Date() >= postponedUntil) else { return }
        busy = true
        checkItem.isEnabled = false
        checkItem.title = "Checking for updates…"
        defer {
            busy = false
            checkItem.isEnabled = !installing
            checkItem.title = installing ? "Starting update…" : release.map { "Update to \($0.version)…" } ?? "Check for Updates…"
        }
        do {
            guard let data = try await UpdateRequest(url: UpdateRelease.api, limit: 1024 * 1024).fetch(),
                  let available = try UpdateRelease.parse(data, installed: installedVersion) else {
                if manual, !stopped { showMessage("No update available", "Installed version: \(installedVersion). No newer stable macOS release is available.") }
                return
            }
            if release?.version != available.version || workspace == nil {
                checkItem.title = "Downloading and verifying update…"
                guard let archive = try await UpdateRequest(url: available.packageURL, limit: UpdateArchive.maximumDownload).fetch(),
                      let checksum = try await UpdateRequest(url: available.checksumURL, limit: 4096).fetch() else {
                    throw UpdateError.invalid("The update package is unavailable.")
                }
                let directory = try await Task.detached {
                    try UpdatesInstaller.prepare(archive: archive, checksum: checksum, release: available)
                }.value
                if let workspace { try? FileManager.default.removeItem(at: workspace) }
                workspace = directory
                release = available
            }
            if stopped {
                if let workspace { try? FileManager.default.removeItem(at: workspace) }
                workspace = nil
                return
            }
            status = "Version \(available.version) is downloaded and SHA-256 verified. Settings, pins, history, and launch-at-login registration stay unchanged.\n\nThe bundle uses a local ad-hoc signature, not a verified publisher signature. macOS may require Accessibility approval again after an update."
            if manual || (canShow() && Date() >= postponedUntil) { offerUpdate() }
        } catch {
            if manual, !stopped { showMessage("Could not prepare an update", "\(error.localizedDescription)\n\nThe current app has not changed. Check for updates to retry.") }
            NSLog("Update check: %@", error.localizedDescription)
        }
    }

    private func offerUpdate() {
        guard let workspace, let release, !installing, !stopped, canShow() else { return }
        let alert = NSAlert()
        alert.messageText = "Update Vertical App Switcher"
        alert.informativeText = status
        alert.addButton(withTitle: "Update and restart")
        alert.addButton(withTitle: "Later")
        NSApp.activate(ignoringOtherApps: true)
        if alert.runModal() == .alertFirstButtonReturn {
            installing = true
            checkItem.isEnabled = false
            checkItem.title = "Starting update…"
            Task {
                do {
                    let target = Bundle.main.bundleURL
                    let plan = try await Task.detached { try UpdatesInstaller.start(workspace: workspace, release: release, target: target) }.value
                    installPlan = plan
                    requestExit()
                } catch {
                    installing = false
                    checkItem.isEnabled = true
                    checkItem.title = "Update to \(release.version)…"
                    showMessage("The update could not start", "\(error.localizedDescription)\n\nThe current app is still open.")
                }
            }
        } else { defaults.set(Date().addingTimeInterval(24 * 60 * 60), forKey: "UpdatePostponedUntil") }
    }

    private func showMessage(_ title: String, _ message: String) {
        guard canShow() else { return }
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.addButton(withTitle: "OK")
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }
}
