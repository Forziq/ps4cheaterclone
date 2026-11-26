using System.Net;

namespace PS4Mem.Api;

/// <summary>
/// The API can write to PS4 memory, so it must never listen on the network.
/// Refuses any listen address that isn't 127.0.0.1, ::1 or localhost.
/// </summary>
public static class LoopbackGuard
{
    public static void EnsureLoopback(string urls)
    {
        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsLoopback(url))
                throw new InvalidOperationException(
                    $"Refusing to listen on '{url}': PS4 Mem's API only listens on 127.0.0.1, [::1] or localhost.");
        }
    }

    public static bool IsLoopback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }
}
