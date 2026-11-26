namespace PS4Mem.Core.Connection;

/// <summary>
/// The command needs a connection and there is none, or connect was called while connected.
/// The API answers these with 409 Conflict.
/// </summary>
public sealed class ConnectionStateException(string message) : InvalidOperationException(message)
{
    public static ConnectionStateException NotConnected() =>
        new("Not connected to the PS4. Connect first.");

    public static ConnectionStateException AlreadyConnected(string endpoint) =>
        new($"Already connected to {endpoint}. Disconnect first.");
}
