import XCTest
@testable import PS4Mem

@MainActor
final class BackendProcessTests: XCTestCase {
    override func setUp() async throws { StubURLProtocol.reset() }
    override func tearDown() async throws { StubURLProtocol.reset() }

    /// A command that must never run (the test fails if it's asked for).
    private func neverLaunch() throws -> BackendProcess.LaunchCommand {
        XCTFail("the backend should not be launched")
        throw APIError.unreachable("not launched")
    }

    private func shell(_ script: String) -> BackendProcess.LaunchCommand {
        .init(executable: URL(fileURLWithPath: "/bin/sh"), arguments: ["-c", script],
              directory: FileManager.default.temporaryDirectory)
    }

    func testUsesAnApiThatAlreadyAnswers() async {
        StubURLProtocol.handler = { _ in .init(status: 200, body: statusJSONDisconnected) }
        let backend = BackendProcess(client: stubClient(), launchCommand: neverLaunch)
        await backend.start()
        XCTAssertEqual(backend.state, .running(ownedByApp: false))
        XCTAssertNil(backend.launchedPid)
        backend.stop() // must not touch an API it didn't start
        XCTAssertEqual(backend.state, .running(ownedByApp: false))
    }

    func testLaunchesAndWaitsUntilItAnswers() async throws {
        // No answer at first; the "backend" starts answering after a few polls.
        nonisolated(unsafe) var calls = 0
        StubURLProtocol.handler = { _ in
            calls += 1
            if calls < 3 { throw URLError(.cannotConnectToHost) }
            return .init(status: 200, body: statusJSONDisconnected)
        }
        let command = shell("sleep 30")
        let backend = BackendProcess(client: stubClient(), launchCommand: { command }, pollInterval: 0.05)
        await backend.start()
        XCTAssertEqual(backend.state, .running(ownedByApp: true))
        let pid = try XCTUnwrap(backend.launchedPid)
        XCTAssertTrue(ProcessTree.isAlive(pid))

        backend.stop()
        XCTAssertNil(backend.launchedPid)
        XCTAssertEqual(backend.state, .idle)
        try await waitUntil { !ProcessTree.isAlive(pid) }
    }

    func testStopAlsoStopsChildProcesses() async throws {
        // Like `dotnet run`: the launched process starts the real server as a child.
        // The child prints its pid to a file so the test can check it's gone afterwards.
        let pidFile = FileManager.default.temporaryDirectory.appendingPathComponent("ps4mem-child-\(UUID()).pid")
        defer { try? FileManager.default.removeItem(at: pidFile) }
        StubURLProtocol.handler = { _ in
            if FileManager.default.fileExists(atPath: pidFile.path) {
                return .init(status: 200, body: statusJSONDisconnected)
            }
            throw URLError(.cannotConnectToHost)
        }
        // `trap '' TERM` makes the child ignore SIGTERM, so it also checks the SIGKILL fallback.
        let command = shell("/bin/sh -c 'trap \"\" TERM; echo $$ > \(pidFile.path); sleep 60' & wait")
        let backend = BackendProcess(client: stubClient(), launchCommand: { command }, pollInterval: 0.05)
        await backend.start()
        XCTAssertEqual(backend.state, .running(ownedByApp: true))
        let text = try String(contentsOf: pidFile, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)
        let childPid = try XCTUnwrap(Int32(text))
        XCTAssertTrue(ProcessTree.isAlive(childPid))

        backend.stop()
        try await waitUntil { !ProcessTree.isAlive(childPid) }
    }

    func testBackendThatExitsEarlyFails() async {
        StubURLProtocol.handler = { _ in throw URLError(.cannotConnectToHost) }
        let command = shell("exit 3")
        let backend = BackendProcess(client: stubClient(), launchCommand: { command }, pollInterval: 0.05)
        await backend.start()
        XCTAssertEqual(backend.state, .failed("The backend stopped (exit code 3). See the terminal for its output."))
    }

    func testBackendThatNeverAnswersTimesOutAndIsStopped() async throws {
        StubURLProtocol.handler = { _ in throw URLError(.cannotConnectToHost) }
        let command = shell("sleep 30")
        let backend = BackendProcess(client: stubClient(), launchCommand: { command },
                                     startTimeout: 0.5, pollInterval: 0.05)
        await backend.start()
        guard case .failed(let message) = backend.state else { return XCTFail("expected failed, got \(backend.state)") }
        XCTAssertTrue(message.contains("didn't answer"), message)
        XCTAssertNil(backend.launchedPid)
    }

    func testLaunchCommandErrorIsShown() async {
        StubURLProtocol.handler = { _ in throw URLError(.cannotConnectToHost) }
        let backend = BackendProcess(client: stubClient(),
                                     launchCommand: { throw BackendProcess.LaunchCommand.LaunchError.noDotnet })
        await backend.start()
        guard case .failed(let message) = backend.state else { return XCTFail("expected failed") }
        XCTAssertTrue(message.contains("dotnet"), message)
    }

    // MARK: Finding the repo and dotnet

    func testFindsRepoAboveTheExecutable() throws {
        let repo = try makeFakeRepo()
        defer { try? FileManager.default.removeItem(at: repo) }
        let exe = repo.appendingPathComponent("mac/.build/debug/PS4Mem").path
        let found = BackendProcess.LaunchCommand.findRepoRoot(environment: [:], startingPoints: [exe])
        XCTAssertEqual(found?.standardizedFileURL.path, repo.standardizedFileURL.path)
    }

    func testRepoRootFromEnvironmentWins() throws {
        let repo = try makeFakeRepo()
        defer { try? FileManager.default.removeItem(at: repo) }
        let found = BackendProcess.LaunchCommand.findRepoRoot(environment: ["PS4MEM_REPO_ROOT": repo.path],
                                                              startingPoints: ["/"])
        XCTAssertEqual(found?.path, repo.path)
    }

    func testNoRepoFound() {
        XCTAssertNil(BackendProcess.LaunchCommand.findRepoRoot(environment: [:], startingPoints: ["/usr/bin"]))
    }

    func testDevelopmentCommandRunsTheApiProject() throws {
        let repo = try makeFakeRepo()
        defer { try? FileManager.default.removeItem(at: repo) }
        let bin = repo.appendingPathComponent("bin")
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let dotnet = bin.appendingPathComponent("dotnet")
        FileManager.default.createFile(atPath: dotnet.path, contents: Data("#!/bin/sh\n".utf8),
                                       attributes: [.posixPermissions: 0o755])

        let command = try BackendProcess.LaunchCommand.development(
            apiURL: URL(string: "http://127.0.0.1:5080")!,
            environment: ["PS4MEM_REPO_ROOT": repo.path, "PATH": bin.path])
        XCTAssertEqual(command.executable.path, dotnet.path)
        XCTAssertEqual(command.arguments, ["run", "--project", "src/PS4Mem.Api", "--urls", "http://127.0.0.1:5080"])
        XCTAssertEqual(command.directory.path, repo.path)
    }

    // MARK: Helpers

    private func makeFakeRepo() throws -> URL {
        let repo = FileManager.default.temporaryDirectory.appendingPathComponent("ps4mem-repo-\(UUID())")
        try FileManager.default.createDirectory(at: repo.appendingPathComponent("mac/.build/debug"),
                                                withIntermediateDirectories: true)
        FileManager.default.createFile(atPath: repo.appendingPathComponent("PS4Mem.sln").path, contents: Data())
        return repo
    }

    private func waitUntil(timeout: TimeInterval = 8, _ condition: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() {
            if Date() > deadline { return XCTFail("timed out waiting") }
            try await Task.sleep(nanoseconds: 50_000_000)
        }
    }
}
