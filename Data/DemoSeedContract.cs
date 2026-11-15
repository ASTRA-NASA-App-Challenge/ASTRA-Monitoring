using System.Security.Cryptography;
using System.Text;

namespace MonitoringApi.Data;

public static class DemoSeedContract
{
    public const string Algorithm = "synthetic-presentation-v1";
    public const int SampleCount = 125;
    public static IReadOnlyList<string> Pins { get; } = Array.AsReadOnly(new[] { "410201", "410202", "520301", "520302", "520303", "520304" });
    public static bool NeedsPhoto(int slot) => slot is 0 or 20 or 76;
    public static string ClientId(string kind, string pin, int slot) => new Guid(SHA256.HashData(
        Encoding.UTF8.GetBytes($"astra-presentation-v1:{kind}:{pin}:{slot}" )).AsSpan(0, 16)).ToString("D");
}
