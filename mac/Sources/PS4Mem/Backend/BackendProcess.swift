import Foundation
import Observation

/// Makes sure the C# backend is running, and stops it again if this app started it.
///
/// - If an API already answers on the port (e.g. you started it yourself with `dotnet run`), the app
///   uses that one and leaves it running when the app quits.
/// - Otherwise it starts one and waits until `GET /api/status` answers: the backend inside PS4Mem.app
///   (`Contents/Resources/backend`), or `dotnet run --project src/PS4Mem.Api` in the repo folder during
///   development. When the app quits, it stops that process and its children.
@MainActor
@Observable
final class BackendProcess {
    enum State: Equatable {
        case idle
        /// Asking the port whether an API already runs there.
        case checking
        /// We launched the backend and are waiting for it to answer (the first build can take a minute).
        case starting
        /// The backend answers. `ownedByApp` is true when this app started it (and so stops it).
        case running(ownedByApp: Bool)
        case failed(String)
    }

    /// How to start the backend: the program, its arguments and the folder it runs in.
    struct LaunchCommand: Equatable {
        var executable: URL
        var arguments: [String]
        var directory: URL
        /// Where the backend's output goes (replaced at each start). Nil: inherited, so it shows in the
        /// terminal that ran ./run.sh.
        var outputLog: URL? = nil

        /// Where to look when the backend fails.
        var outputHint: String {
            outputLog.map { "See \($0.path) for its output." } ?? "See the terminal for its output."
        }
    }

    private(set) var state: State = .idle

    let client: APIClient
    private let launchCommand: () throws -> LaunchCommand
    private let startTimeout: TimeInterval
    private let pollInterval: TimeInterval
    private var process: Process?
    /// Where the running (or last) backend writes its output, for the error messages.
    private var outputHint = "See the terminal for its output."

    /// - Parameters:
    ///   - launchCommand: builds the command when it's needed (it can fail, e.g. no dotnet found).
    ///   - startTimeout: how long to wait for a launched backend to answer. `dotnet run` builds first.
    init(client: APIClient, launchCommand: @escaping () throws -> LaunchCommand,
         startTimeout: TimeInterval = 180, pollInterval: TimeInterval = 0.5) {
        self.client = client
        self.launchCommand = launchCommand
        self.startTimeout = startTimeout
        self.pollInterval = pollInterval
    }

    var isRunning: Bool {
        if case .running = state { return true }
        return false
    }

    /// The process ID of the backend this app started (nil if we use one that was already running).
    var launchedPid: Int32? { process?.processIdentifier }

    /// Uses a running API or starts one. Returns when it answers, or with `state = .failed(...)`.
    func start() async {
        switch state {
        case .checking, .starting, .running: return
        case .idle, .failed: break
        }

        state = .checking
        if await answers() {
            log("an API already answers at \(client.baseURL.absoluteString); using it")
            state = .running(ownedByApp: false)
            return
        }

        let command: LaunchCommand
        do { command = try launchCommand() } catch {
            state = .failed(error.localizedDescription)
            return
        }

        let process = Process()
        process.executableURL = command.executable
        process.arguments = command.arguments
        process.currentDirectoryURL = command.directory
        outputHint = command.outputHint
        var output: FileHandle?
        if let outputLog = command.outputLog {
            // Started from Finder there's no terminal, so the backend's output goes to a file.
            do {
                output = try Self.openOutputLog(outputLog)
                process.standardOutput = output
                process.standardError = output
            } catch {
                log("can't write \(outputLog.path) (\(error.localizedDescription)); backend output is lost")
            }
        }
        // Otherwise stdout and stderr are inherited, so the backend's log shows in the terminal that ran ./run.sh.
        process.terminationHandler = { [weak self] finished in
            let code = finished.terminationStatus
            Task { @MainActor in self?.processEnded(finished, code: code) }
        }
        // The backend has its own copy of the log file now; this one isn't needed.
        defer { try? output?.close() }
        do {
            try process.run()
        } catch {
            state = .failed("Couldn't start the backend (\(command.executable.path)): \(error.localizedDescription)")
            return
        }
        self.process = process
        state = .starting
        log("started backend pid \(process.processIdentifier): \(command.executable.path) \(command.arguments.joined(separator: " "))")

        let deadline = Date().addingTimeInterval(startTimeout)
        while Date() < deadline {
            if self.process == nil { return } // it exited; processEnded set the state
            if await answers() {
                state = .running(ownedByApp: true)
                log("backend answers at \(client.baseURL.absoluteString)")
                return
            }
            try? await Task.sleep(nanoseconds: UInt64(pollInterval * 1_000_000_000))
        }
        stop()
        state = .failed("The backend didn't answer within \(Int(startTimeout)) s. \(outputHint)")
    }

    /// Stops the backend if this app started it (it and every child process, e.g. the API under `dotnet run`).
    /// Blocks for up to ~5 s so it can also run while the app quits.
    func stop() {
        guard let process else { return }
        self.process = nil
        let pid = process.processIdentifier
        if process.isRunning {
            log("stopping backend pid \(pid)")
            ProcessTree.terminate(pid)
        }
        if case .running(ownedByApp: true) = state { state = .idle }
        if case .starting = state { state = .idle }
    }

    private func processEnded(_ finished: Process, code: Int32) {
        guard finished === process else { return } // stop() already handled it
        process = nil
        state = .failed("The backend stopped (exit code \(code)). \(outputHint)")
        log("backend exited with code \(code)")
    }

    /// True when `GET /api/status` gets any answer that decodes.
    private func answers() async -> Bool {
        do {
            _ = try await client.status()
            return true
        } catch {
            return false
        }
    }

    /// An empty `file` (its folder is created if needed), open for writing.
    private static func openOutputLog(_ file: URL) throws -> FileHandle {
        try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard FileManager.default.createFile(atPath: file.path, contents: nil) else {
            throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: file.path])
        }
        return try FileHandle(forWritingTo: file)
    }

    private func log(_ message: String) {
        FileHandle.standardError.write(Data("[app] \(message)\n".utf8))
    }
}

// MARK: - Launch commands

extension BackendProcess.LaunchCommand {
    /// The backend inside PS4Mem.app if there is one, otherwise the development command.
    static func automatic(apiURL: URL) throws -> Self {
        if let bundled = bundled(apiURL: apiURL) { return bundled }
        return try development(apiURL: apiURL)
    }

    /// The self-contained backend that scripts/build-app.sh puts in PS4Mem.app
    /// (`Contents/Resources/backend/PS4Mem.Api`). No .NET install is needed to run it.
    /// Nil when there's none, e.g. when the app runs from `swift build`.
    ///
    /// It runs in its own folder (so the bundled payload in `backend/payloads` is found), writes its
    /// output and debug logs to `logs` (not into the app), and stops by itself when `parentPid` exits.
    static func bundled(apiURL: URL, resources: URL? = Bundle.main.resourceURL,
                        logs: URL = defaultLogFolder, parentPid: Int32 = getpid()) -> Self? {
        guard let backend = resources?.appendingPathComponent("backend") else { return nil }
        let executable = backend.appendingPathComponent("PS4Mem.Api")
        guard FileManager.default.isExecutableFile(atPath: executable.path) else { return nil }
        return .init(executable: executable,
                     arguments: ["--urls", apiURL.absoluteString,
                                 "--PS4Mem:LogDirectory", logs.path,
                                 "--PS4Mem:ParentPid", String(parentPid)],
                     directory: backend,
                     outputLog: logs.appendingPathComponent("backend.log"))
    }

    /// `~/Library/Logs/PS4Mem`: the bundled backend's output and debug logs (Console.app shows it too).
    static var defaultLogFolder: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/PS4Mem", isDirectory: true)
    }
}

// MARK: - Development launch command

extension BackendProcess.LaunchCommand {
    enum LaunchError: LocalizedError {
        case noRepo
        case noDotnet

        var errorDescription: String? {
            switch self {
            case .noRepo:
                return "Couldn't find the PS4 Mem repo (PS4Mem.sln) to start the backend. " +
                    "Run the app with ./run.sh, or set PS4MEM_REPO_ROOT."
            case .noDotnet:
                return "Couldn't find `dotnet`. Install the .NET 10 SDK, or start the API yourself: " +
                    "dotnet run --project src/PS4Mem.Api"
            }
        }
    }

    /// `dotnet run --project src/PS4Mem.Api --urls <url>` in the repo folder.
    static func development(apiURL: URL, environment: [String: String] = ProcessInfo.processInfo.environment,
                            executablePath: String = CommandLine.arguments.first ?? "",
                            currentDirectory: String = FileManager.default.currentDirectoryPath) throws -> Self {
        guard let repo = findRepoRoot(environment: environment, startingPoints: [executablePath, currentDirectory]) else {
            throw LaunchError.noRepo
        }
        guard let dotnet = findDotnet(environment: environment) else { throw LaunchError.noDotnet }
        return .init(executable: dotnet,
                     arguments: ["run", "--project", "src/PS4Mem.Api", "--urls", apiURL.absoluteString],
                     directory: repo)
    }

    /// PS4MEM_REPO_ROOT if set, else the first folder above one of `startingPoints` that holds PS4Mem.sln.
    static func findRepoRoot(environment: [String: String], startingPoints: [String]) -> URL? {
        let files = FileManager.default
        if let root = environment["PS4MEM_REPO_ROOT"], !root.isEmpty {
            let url = URL(fileURLWithPath: root)
            return files.fileExists(atPath: url.appendingPathComponent("PS4Mem.sln").path) ? url : nil
        }
        for start in startingPoints where !start.isEmpty {
            var folder = URL(fileURLWithPath: start).standardizedFileURL
            for _ in 0..<12 {
                if files.fileExists(atPath: folder.appendingPathComponent("PS4Mem.sln").path) { return folder }
                let parent = folder.deletingLastPathComponent()
                if parent.path == folder.path { break }
                folder = parent
            }
        }
        return nil
    }

    /// `dotnet` from PATH, or from the places the .NET installers and Homebrew put it.
    static func findDotnet(environment: [String: String]) -> URL? {
        var folders = (environment["PATH"] ?? "").split(separator: ":").map(String.init)
        if let root = environment["DOTNET_ROOT"] { folders.append(root) }
        if let home = environment["HOME"] { folders.append("\(home)/.dotnet") }
        folders += ["/usr/local/share/dotnet", "/opt/homebrew/bin", "/usr/local/bin"]
        for folder in folders where !folder.isEmpty {
            let path = (folder as NSString).appendingPathComponent("dotnet")
            if FileManager.default.isExecutableFile(atPath: path) { return URL(fileURLWithPath: path) }
        }
        return nil
    }
}

// MARK: - Stopping a process and its children

/// `dotnet run` starts the API as a child process, so stopping only `dotnet` could leave the API running.
/// This finds every descendant (with `pgrep -P`), asks them all to stop (SIGTERM), and kills (SIGKILL)
/// whatever is still alive after the grace period.
enum ProcessTree {
    static func terminate(_ pid: Int32, grace: TimeInterval = 5) {
        let all = [pid] + descendants(of: pid)
        for p in all { kill(p, SIGTERM) }

        let deadline = Date().addingTimeInterval(grace)
        while Date() < deadline, all.contains(where: isAlive) {
            usleep(100_000)
        }
        for p in all where isAlive(p) { kill(p, SIGKILL) }
    }

    /// Children, grandchildren and so on.
    static func descendants(of pid: Int32) -> [Int32] {
        var found: [Int32] = []
        var queue = [pid]
        while let parent = queue.popLast() {
            let children = childPids(of: parent)
            found += children
            queue += children
        }
        return found
    }

    static func isAlive(_ pid: Int32) -> Bool {
        // Signal 0 checks the process exists without touching it. A zombie still counts until reaped.
        kill(pid, 0) == 0
    }

    private static func childPids(of pid: Int32) -> [Int32] {
        let pgrep = Process()
        pgrep.executableURL = URL(fileURLWithPath: "/usr/bin/pgrep")
        pgrep.arguments = ["-P", String(pid)]
        let output = Pipe()
        pgrep.standardOutput = output
        pgrep.standardError = FileHandle.nullDevice
        do { try pgrep.run() } catch { return [] }
        let data = output.fileHandleForReading.readDataToEndOfFile()
        pgrep.waitUntilExit()
        return String(decoding: data, as: UTF8.self)
            .split(whereSeparator: \.isNewline)
            .compactMap { Int32($0.trimmingCharacters(in: .whitespaces)) }
    }
}
