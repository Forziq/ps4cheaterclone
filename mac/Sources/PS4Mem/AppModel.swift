import Foundation
import Observation

/// Everything the window shows, in one place. Views read these properties and call these methods;
/// SwiftUI redraws by itself when a property changes (that's what `@Observable` does).
/// `@MainActor` means it's only touched on the main thread, so there are no threading surprises.
@MainActor
@Observable
final class AppModel {
    /// Where the backend listens. PS4MEM_API_URL overrides it (the API's default is the same address).
    nonisolated static let apiURL: URL = {
        let text = ProcessInfo.processInfo.environment["PS4MEM_API_URL"] ?? "http://127.0.0.1:5080"
        return URL(string: text) ?? URL(string: "http://127.0.0.1:5080")!
    }()

    let client: APIClient
    let backend: BackendProcess

    init(client: APIClient = APIClient(baseURL: AppModel.apiURL), backend: BackendProcess? = nil) {
        self.client = client
        self.backend = backend ?? BackendProcess(client: client) {
            try .development(apiURL: client.baseURL)
        }
    }

    /// Called once when the window appears.
    func startUp() async {
        await backend.start()
    }

    /// Called when the app quits (also on Ctrl+C in the terminal).
    func shutDown() {
        backend.stop()
    }
}
