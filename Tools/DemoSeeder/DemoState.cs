using System.Text.Json;

namespace Astra.Demo;

public sealed class DemoState
{
    public int Version { get; set; } = 1;
    public DateTimeOffset Anchor { get; set; }
    public DateTimeOffset Presentation { get; set; }
    public string? UnoId { get; set; }
    public string? CameraId { get; set; }
    public string? UnoKey { get; set; }
    public string? CameraKey { get; set; }
    public bool OwnsPair { get; set; }
    public bool Prepared { get; set; }
    public int CadenceVersion { get; set; }
    public Dictionary<string, string> AssetHashes { get; set; } = [];

    public static DemoState Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<DemoState>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Invalid demo state.")
        : new DemoState { Anchor = DateTimeOffset.UtcNow, Presentation = DateTimeOffset.UtcNow };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
