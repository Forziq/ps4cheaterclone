import AppKit
import SwiftUI

/// The app's entry point. `@main` tells Swift to start here.
@main
struct PS4MemApp: App {
    // AppKit hooks SwiftUI doesn't offer: show a Dock icon when run from the terminal, stop the backend on quit.
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        WindowGroup("PS4 Mem") {
            ContentView(model: appDelegate.model)
                .frame(minWidth: 820, minHeight: 520)
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let model = AppModel()
    private var signalSources: [DispatchSourceSignal] = []

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Run from the terminal (swift build / ./run.sh) the binary isn't an .app bundle, so ask macOS
        // to treat it as a normal app: Dock icon, menu bar, and its window in front.
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        stopBackendOnSignals()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationWillTerminate(_ notification: Notification) {
        model.shutDown()
    }

    /// Ctrl+C in the terminal sends SIGINT; `kill` sends SIGTERM. Either way, stop the backend first.
    private func stopBackendOnSignals() {
        for sig in [SIGINT, SIGTERM] {
            signal(sig, SIG_IGN) // let the dispatch source below handle it instead of dying at once
            let source = DispatchSource.makeSignalSource(signal: sig, queue: .main)
            source.setEventHandler { [weak self] in
                MainActor.assumeIsolated {
                    self?.model.shutDown()
                    exit(0)
                }
            }
            source.resume()
            signalSources.append(source)
        }
    }
}
