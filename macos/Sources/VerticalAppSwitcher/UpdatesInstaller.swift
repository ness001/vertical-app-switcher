import AppKit
import Darwin
import Foundation
import zlib

// Validate ZIP metadata before invoking the system extractor. This contract deliberately
// excludes links, resource forks, encryption, ZIP64, and arbitrary bundle payloads.
enum UpdateArchive {
    static let maximumDownload = 128 * 1024 * 1024
    static let maximumExpanded = 256 * 1024 * 1024
    static let appName = "Vertical App Switcher.app"
    static let files: Set<String> = ["Contents/Info.plist", "Contents/MacOS/VerticalAppSwitcher",
        "Contents/Resources/LICENSE", "Contents/Resources/NOTICE", "Contents/_CodeSignature/CodeResources"]
    static let directories: Set<String> = ["", "Contents/", "Contents/MacOS/", "Contents/Resources/", "Contents/_CodeSignature/"]

    static func validate(_ data: Data) throws {
        func fail() -> UpdateError { .invalid("The update ZIP has an unsupported or unsafe layout.") }
        func number(_ offset: Int, _ count: Int) throws -> UInt64 {
            guard offset >= 0, count <= data.count, offset <= data.count - count else { throw fail() }
            return (0..<count).reduce(0) { $0 | UInt64(data[offset + $1]) << ($1 * 8) }
        }
        guard data.count >= 22, data.count <= maximumDownload else { throw fail() }
        // Release ZIPs have no archive comment; rejecting it makes the end record unambiguous.
        let end = data.count - 22
        guard try number(end, 4) == 0x06054b50,
              try number(end + 4, 2) == 0, try number(end + 6, 2) == 0,
              try number(end + 8, 2) == number(end + 10, 2),
              try number(end + 20, 2) == 0 else { throw fail() }
        let count = Int(try number(end + 10, 2))
        let centralSize = Int(try number(end + 12, 4))
        let centralStart = Int(try number(end + 16, 4))
        guard (1...32).contains(count), centralStart + centralSize == end else { throw fail() }
        var cursor = centralStart
        var total = 0
        var seen = Set<String>()
        var regions: [Range<Int>] = []
        for _ in 0..<count {
            guard try number(cursor, 4) == 0x02014b50 else { throw fail() }
            let flags = try number(cursor + 8, 2)
            let method = try number(cursor + 10, 2)
            let compressed = Int(try number(cursor + 20, 4))
            let expanded = Int(try number(cursor + 24, 4))
            let nameLength = Int(try number(cursor + 28, 2))
            let extraLength = Int(try number(cursor + 30, 2))
            let commentLength = Int(try number(cursor + 32, 2))
            let attributes = try number(cursor + 38, 4)
            let local = Int(try number(cursor + 42, 4))
            let next = cursor + 46 + nameLength + extraLength + commentLength
            guard flags & ~UInt64(0x080e) == 0, [UInt64(0), 8].contains(method),
                  extraLength == 0, commentLength == 0, try number(cursor + 34, 2) == 0,
                  next <= end, nameLength > 0 else { throw fail() }
            let nameData = data.subdata(in: (cursor + 46)..<(cursor + 46 + nameLength))
            guard nameData.allSatisfy({ $0 >= 32 && $0 < 127 }), let name = String(data: nameData, encoding: .utf8),
                  name.hasPrefix(appName + "/"), seen.insert(name).inserted else { throw fail() }
            let relative = String(name.dropFirst(appName.count + 1))
            let directory = name.hasSuffix("/")
            guard directory ? directories.contains(relative) : files.contains(relative) else { throw fail() }
            let kind = (attributes >> 16) & 0xf000
            guard kind == 0 || kind == (directory ? 0x4000 : 0x8000),
                  !directory || (expanded == 0 && compressed == 0) else { throw fail() }
            total += expanded
            guard total <= maximumExpanded,
                  try number(local, 4) == 0x04034b50,
                  try number(local + 6, 2) == flags, try number(local + 8, 2) == method,
                  try number(local + 26, 2) == UInt64(nameLength), try number(local + 28, 2) == 0 else { throw fail() }
            let payload = local + 30 + nameLength
            guard payload <= centralStart, compressed <= centralStart - payload,
                  data.subdata(in: (local + 30)..<payload) == nameData else { throw fail() }
            if flags & 8 == 0 {
                guard try number(local + 14, 4) == number(cursor + 16, 4),
                      try number(local + 18, 4) == UInt64(compressed),
                      try number(local + 22, 4) == UInt64(expanded) else { throw fail() }
            }
            try verifyPayload(data, offset: payload, compressed: compressed, expanded: expanded,
                              checksum: UInt32(try number(cursor + 16, 4)), method: method)
            regions.append(local..<(payload + compressed))
            cursor = next
        }
        guard cursor == end, files.subtracting(["Contents/_CodeSignature/CodeResources"]).allSatisfy({ seen.contains(appName + "/" + $0) }) else { throw fail() }
        let sorted = regions.sorted { $0.lowerBound < $1.lowerBound }
        guard sorted.first?.lowerBound == 0 else { throw fail() }
        for index in 1..<sorted.count where sorted[index].lowerBound < sorted[index - 1].upperBound { throw fail() }
    }

    private static func verifyPayload(_ data: Data, offset: Int, compressed: Int, expanded: Int,
                                      checksum: UInt32, method: UInt64) throws {
        let invalid = UpdateError.invalid("The ZIP payload exceeds its declared size or fails its CRC check.")
        try data.withUnsafeBytes { (bytes: UnsafeRawBufferPointer) in
            let source = bytes.baseAddress!.advanced(by: offset).assumingMemoryBound(to: Bytef.self)
            if method == 0 {
                guard compressed == expanded, UInt32(crc32(0, source, uInt(compressed))) == checksum else { throw invalid }
                return
            }
            var stream = z_stream()
            guard inflateInit2_(&stream, -MAX_WBITS, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else { throw invalid }
            defer { inflateEnd(&stream) }
            stream.next_in = UnsafeMutablePointer(mutating: source)
            stream.avail_in = uInt(compressed)
            var buffer = [UInt8](repeating: 0, count: 65536)
            var crc: uLong = 0
            var result: Int32 = Z_OK
            repeat {
                result = buffer.withUnsafeMutableBufferPointer { output in
                    stream.next_out = output.baseAddress
                    stream.avail_out = uInt(output.count)
                    let result = inflate(&stream, Z_NO_FLUSH)
                    let produced = output.count - Int(stream.avail_out)
                    crc = crc32(crc, output.baseAddress, uInt(produced))
                    return result
                }
                guard stream.total_out <= expanded, result == Z_OK || result == Z_STREAM_END else { throw invalid }
            } while result != Z_STREAM_END
            guard stream.total_out == expanded, stream.total_in == compressed, UInt32(crc) == checksum else { throw invalid }
        }
    }
}

enum UpdatesInstaller {
    private struct Plan: Codable {
        let parentPID: Int32
        let parentLaunched: Date
        let target: URL
        let version: String
        let packageName: String
        let token: String
    }

    private static let manager = FileManager.default
    private static var root: URL {
        manager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("VerticalAppSwitcher/Updates", isDirectory: true)
    }

    static func prepare(archive: Data, checksum: Data, release: UpdateRelease) throws -> URL {
        try manager.createDirectory(at: root, withIntermediateDirectories: true)
        let workspace = root.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try manager.createDirectory(at: workspace, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        do {
            try archive.write(to: workspace.appendingPathComponent(release.packageName), options: .withoutOverwriting)
            try checksum.write(to: workspace.appendingPathComponent("checksum.txt"), options: .withoutOverwriting)
            let verified = try extract(workspace: workspace, packageName: release.packageName, version: release.version.description)
            try manager.removeItem(at: verified.deletingLastPathComponent())
            return workspace
        } catch {
            try? manager.removeItem(at: workspace)
            throw error
        }
    }

    private static func extract(workspace: URL, packageName: String, version: String) throws -> URL {
        let archiveURL = workspace.appendingPathComponent(packageName)
        let size = try archiveURL.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? Int.max
        guard size <= UpdateArchive.maximumDownload else { throw UpdateError.invalid("The update archive is too large.") }
        let data = try Data(contentsOf: archiveURL, options: .mappedIfSafe)
        try UpdateRelease.verifyChecksum(data, checksum: Data(contentsOf: workspace.appendingPathComponent("checksum.txt")), packageName: packageName)
        try UpdateArchive.validate(data)
        let output = workspace.appendingPathComponent("verified-\(UUID().uuidString)", isDirectory: true)
        try manager.createDirectory(at: output, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        try command("/usr/bin/unzip", ["-q", archiveURL.path, "-d", output.path])
        let bundle = output.appendingPathComponent(UpdateArchive.appName, isDirectory: true)
        try validateBundle(bundle, version: version)
        return bundle
    }

    private static func validateBundle(_ bundle: URL, version: String) throws {
        guard let enumerator = manager.enumerator(at: bundle, includingPropertiesForKeys: [.isSymbolicLinkKey, .isRegularFileKey, .isDirectoryKey, .fileSizeKey]) else {
            throw UpdateError.invalid("The update bundle is missing.")
        }
        for case let file as URL in enumerator {
            let values = try file.resourceValues(forKeys: [.isSymbolicLinkKey, .isRegularFileKey, .isDirectoryKey, .fileSizeKey])
            guard values.isSymbolicLink == false, values.isRegularFile == true || values.isDirectory == true else {
                throw UpdateError.invalid("The update bundle contains a link or special file.")
            }
        }
        let infoURL = bundle.appendingPathComponent("Contents/Info.plist")
        let infoData = try Data(contentsOf: infoURL)
        guard infoData.count <= 65536,
              let info = try PropertyListSerialization.propertyList(from: infoData, format: nil) as? [String: Any],
              info["CFBundleIdentifier"] as? String == "com.ness001.VerticalAppSwitcher",
              info["CFBundleExecutable"] as? String == "VerticalAppSwitcher",
              info["CFBundlePackageType"] as? String == "APPL",
              info["CFBundleShortVersionString"] as? String == version,
              (info["VerticalAppSwitcherReleaseVersion"] as? String ?? version) == version else {
            throw UpdateError.invalid("The update bundle identity or version does not match the release.")
        }
        if let minimum = info["LSMinimumSystemVersion"] as? String {
            let fields = minimum.split(separator: ".", omittingEmptySubsequences: false)
            let parts = fields.compactMap { Int($0) }
            guard fields.count == parts.count, (2...3).contains(parts.count), parts.allSatisfy({ $0 >= 0 }),
                  ProcessInfo.processInfo.isOperatingSystemAtLeast(OperatingSystemVersion(majorVersion: parts[0], minorVersion: parts[1], patchVersion: parts.count == 3 ? parts[2] : 0)) else {
                throw UpdateError.invalid("The update requires a newer macOS version.")
            }
        }
        try command("/usr/bin/lipo", ["-verify_arch", UpdateRelease.architecture == "x64" ? "x86_64" : "arm64", bundle.appendingPathComponent("Contents/MacOS/VerticalAppSwitcher").path])
        try command("/usr/bin/codesign", ["--verify", "--deep", "--strict", bundle.path])
    }

    static func start(workspace source: URL, release: UpdateRelease, target: URL) throws -> URL {
        try validateWorkspace(source)
        // Retries never overwrite an earlier helper's executable, plan, or handshake files.
        let workspace = root.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try manager.createDirectory(at: workspace, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        var helperStarted = false
        defer {
            if !helperStarted { try? manager.removeItem(at: workspace) }
        }
        try manager.copyItem(at: source.appendingPathComponent(release.packageName), to: workspace.appendingPathComponent(release.packageName))
        try manager.copyItem(at: source.appendingPathComponent("checksum.txt"), to: workspace.appendingPathComponent("checksum.txt"))
        guard target.pathExtension == "app", target.standardizedFileURL == target.resolvingSymlinksInPath(),
              let current = NSRunningApplication(processIdentifier: getpid()), current.bundleURL == target,
              let launched = current.launchDate,
              Bundle(url: target)?.bundleIdentifier == "com.ness001.VerticalAppSwitcher" else {
            throw UpdateError.invalid("Updates require a writable, installed app bundle, not a command-line build or linked location.")
        }
        let probe = target.deletingLastPathComponent().appendingPathComponent(".vertical-update-probe-\(UUID().uuidString)")
        try Data().write(to: probe, options: .withoutOverwriting)
        try manager.removeItem(at: probe)
        let helper = workspace.appendingPathComponent("UpdateHelper")
        if manager.fileExists(atPath: helper.path) { try manager.removeItem(at: helper) }
        try manager.copyItem(at: target.appendingPathComponent("Contents/MacOS/VerticalAppSwitcher"), to: helper)
        let plan = Plan(parentPID: getpid(), parentLaunched: launched, target: target,
                        version: release.version.description, packageName: release.packageName, token: UUID().uuidString)
        let planURL = workspace.appendingPathComponent("plan.json")
        try JSONEncoder().encode(plan).write(to: planURL, options: .atomic)
        let process = Process()
        process.executableURL = helper
        process.arguments = ["--apply-update", planURL.path]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        helperStarted = true
        let deadline = Date().addingTimeInterval(30)
        let ready = workspace.appendingPathComponent("ready")
        while Date() < deadline, process.isRunning {
            if (try? String(contentsOf: ready, encoding: .utf8)) == plan.token {
                try Data(plan.token.utf8).write(to: workspace.appendingPathComponent("approved"), options: .atomic)
                return planURL
            }
            Thread.sleep(forTimeInterval: 0.1)
        }
        throw UpdateError.invalid("The update helper did not become ready. No app files were changed. Retry after checking \(workspace.path)/update.log.")
    }

    // Invoke before creating NSApplication. The helper never creates a UI or alters settings.
    static func runHelperIfRequested() -> Bool {
        guard CommandLine.arguments.contains("--apply-update") else { return false }
        guard CommandLine.arguments.count == 3, CommandLine.arguments[1] == "--apply-update" else { return true }
        let planURL = URL(fileURLWithPath: CommandLine.arguments[2])
        let workspace = planURL.deletingLastPathComponent()
        do {
            try validateWorkspace(workspace)
            guard planURL.lastPathComponent == "plan.json",
                  URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL == workspace.appendingPathComponent("UpdateHelper") else {
                throw UpdateError.invalid("Invalid updater helper location.")
            }
            let plan = try JSONDecoder().decode(Plan.self, from: Data(contentsOf: planURL))
            try run(plan, workspace: workspace)
        } catch {
            NSLog("Update installation: %@", error.localizedDescription)
            if (try? validateWorkspace(workspace)) != nil {
                try? Data(error.localizedDescription.utf8).write(to: workspace.appendingPathComponent("update.log"), options: .atomic)
            }
        }
        return true
    }

    private static func validateWorkspace(_ workspace: URL) throws {
        guard workspace.standardizedFileURL.deletingLastPathComponent() == root.standardizedFileURL,
              workspace.standardizedFileURL == workspace.resolvingSymlinksInPath(),
              UUID(uuidString: workspace.lastPathComponent) != nil else {
            throw UpdateError.invalid("The update workspace is invalid.")
        }
    }

    private static func run(_ plan: Plan, workspace: URL) throws {
        let version = try UpdateVersion(plan.version)
        guard UUID(uuidString: plan.token) != nil,
              ["osx-universal", "osx-\(UpdateRelease.architecture)"].contains(where: { plan.packageName == "VerticalAppSwitcher-\(version)-\($0).zip" }),
              plan.target.pathExtension == "app", plan.target.standardizedFileURL == plan.target.resolvingSymlinksInPath(),
              Bundle(url: plan.target)?.bundleIdentifier == "com.ness001.VerticalAppSwitcher",
              let installedBundle = Bundle(url: plan.target),
              try UpdateRelease.isNewerStable(version, than: UpdateRelease.installedVersion(in: installedBundle)),
              let parent = NSRunningApplication(processIdentifier: plan.parentPID),
              parent.bundleURL == plan.target, parent.launchDate == plan.parentLaunched else {
            throw UpdateError.invalid("The update process or target does not match the approved request.")
        }
        // The OS releases this lock even if a helper crashes; never unlink a live lock inode.
        let gate = plan.target.deletingLastPathComponent().appendingPathComponent(".VerticalAppSwitcher-update-lock")
        let descriptor = Darwin.open(gate.path, O_CREAT | O_RDWR | O_NOFOLLOW | O_CLOEXEC, mode_t(0o600))
        guard descriptor >= 0 else { throw UpdateError.invalid("The app's update lock is not writable.") }
        defer { Darwin.close(descriptor) }
        guard flock(descriptor, LOCK_EX | LOCK_NB) == 0 else { throw UpdateError.invalid("Another update is already in progress.") }
        defer { flock(descriptor, LOCK_UN) }
        defer {
            // Keep only the small diagnostic/handshake files after this helper finishes.
            if let files = try? manager.contentsOfDirectory(at: workspace, includingPropertiesForKeys: nil) {
                for file in files where file.lastPathComponent == plan.packageName || file.lastPathComponent == "UpdateHelper" || file.lastPathComponent.hasPrefix("verified-") {
                    try? manager.removeItem(at: file)
                }
            }
        }
        let verified = try extract(workspace: workspace, packageName: plan.packageName, version: plan.version)
        let staged = plan.target.deletingLastPathComponent().appendingPathComponent(".VerticalAppSwitcher-update-\(plan.token).app")
        let backup = plan.target.deletingLastPathComponent().appendingPathComponent(".VerticalAppSwitcher-backup-\(plan.token).app")
        try manager.copyItem(at: verified, to: staged)
        defer { try? manager.removeItem(at: staged) }
        try validateBundle(staged, version: plan.version)
        try Data(plan.token.utf8).write(to: workspace.appendingPathComponent("ready"), options: .atomic)
        let deadline = Date().addingTimeInterval(60)
        while !parent.isTerminated, Date() < deadline { RunLoop.current.run(until: Date().addingTimeInterval(0.1)) }
        guard parent.isTerminated,
              (try? String(contentsOf: workspace.appendingPathComponent("exited"), encoding: .utf8)) == "normal-exit",
              (try? String(contentsOf: workspace.appendingPathComponent("approved"), encoding: .utf8)) == plan.token,
              !NSWorkspace.shared.runningApplications.contains(where: { $0.bundleURL == plan.target && !$0.isTerminated }) else {
            throw UpdateError.invalid("The app did not exit normally or another copy is running. No app files were replaced.")
        }
        try manager.moveItem(at: plan.target, to: backup)
        do {
            try manager.moveItem(at: staged, to: plan.target)
            let result = try restart(plan.target, workspace: workspace, token: plan.token)
            switch result {
            case .ready:
                try Data("Installed \(version). Previous app retained at \(backup.path).".utf8).write(to: workspace.appendingPathComponent("update.log"), options: .atomic)
            case .stillRunning:
                throw RestartUncertain(backup: backup)
            case .failed:
                guard !NSWorkspace.shared.runningApplications.contains(where: { $0.bundleURL == plan.target && !$0.isTerminated }) else {
                    throw RestartUncertain(backup: backup)
                }
                try manager.removeItem(at: plan.target)
                try manager.moveItem(at: backup, to: plan.target)
                _ = try restart(plan.target, workspace: workspace, token: UUID().uuidString)
                throw UpdateError.invalid("The updated app exited before confirming startup. The previous app was restored.")
            }
        } catch let uncertain as RestartUncertain {
            // LaunchServices may still be dispatching a timed-out launch request.
            throw uncertain
        } catch {
            // Never replace a bundle under an app that may still be starting.
            if manager.fileExists(atPath: backup.path), !NSWorkspace.shared.runningApplications.contains(where: { $0.bundleURL == plan.target && !$0.isTerminated }) {
                if manager.fileExists(atPath: plan.target.path) { try manager.removeItem(at: plan.target) }
                try manager.moveItem(at: backup, to: plan.target)
                _ = try? restart(plan.target, workspace: workspace, token: UUID().uuidString)
            }
            throw error
        }
    }

    private struct RestartUncertain: LocalizedError {
        let backup: URL
        var errorDescription: String? {
            "The updated app did not confirm startup and may still be starting. Close it normally before restoring \(backup.path)."
        }
    }

    private enum RestartResult { case ready, failed, stillRunning }

    private static func restart(_ target: URL, workspace: URL, token: String) throws -> RestartResult {
        let config = NSWorkspace.OpenConfiguration()
        config.activates = false
        config.arguments = ["--background", "--update-ready", workspace.path, token]
        var completed = false
        var application: NSRunningApplication?
        var launchError: Error?
        NSWorkspace.shared.openApplication(at: target, configuration: config) { app, error in
            DispatchQueue.main.async {
                application = app
                launchError = error
                completed = true
            }
        }
        let launchDeadline = Date().addingTimeInterval(20)
        while !completed, Date() < launchDeadline { RunLoop.current.run(until: Date().addingTimeInterval(0.1)) }
        guard completed else { return .stillRunning }
        if let launchError { throw launchError }
        guard let application else { return .failed }
        let deadline = Date().addingTimeInterval(20)
        while Date() < deadline {
            if (try? String(contentsOf: workspace.appendingPathComponent("started-\(token)"), encoding: .utf8)) == "ready" { return .ready }
            if application.isTerminated { return .failed }
            RunLoop.current.run(until: Date().addingTimeInterval(0.1))
        }
        return application.isTerminated ? .failed : .stillRunning
    }

    // Call only after normal app initialization has completed successfully.
    static func confirmStartup() {
        let args = CommandLine.arguments
        guard let index = args.firstIndex(of: "--update-ready"), args.count > index + 2,
              UUID(uuidString: args[index + 2]) != nil else { return }
        let workspace = URL(fileURLWithPath: args[index + 1])
        guard (try? validateWorkspace(workspace)) != nil else { return }
        try? Data("ready".utf8).write(to: workspace.appendingPathComponent("started-\(args[index + 2])"), options: .atomic)
    }

    @discardableResult
    private static func command(_ executable: String, _ arguments: [String]) throws -> String {
        let output = manager.temporaryDirectory.appendingPathComponent("vertical-update-command-\(UUID().uuidString)")
        guard manager.createFile(atPath: output.path, contents: nil, attributes: [.posixPermissions: 0o600]) else {
            throw UpdateError.invalid("Could not create update verification output.")
        }
        defer { try? manager.removeItem(at: output) }
        let handle = try FileHandle(forWritingTo: output)
        defer { try? handle.close() }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        process.standardOutput = handle
        process.standardError = handle
        try process.run()
        process.waitUntilExit()
        let text = String(data: (try Data(contentsOf: output)).prefix(8192), encoding: .utf8) ?? ""
        guard process.terminationStatus == 0 else { throw UpdateError.invalid("Update verification failed: \(text)") }
        return text
    }
}
