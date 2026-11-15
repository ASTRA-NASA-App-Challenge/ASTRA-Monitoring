// Unmodified prototype boundaries from ASTRA-Web/Common/Models/HealthSeverity.cs, 2026-11-15.
// Used only when the sibling checkout is absent. When present, the project compiles the actual web source instead.
namespace Common.Models;

public static class HealthSeverity
{
    public static int Rank(string state) => state switch { "good" => 0, "watch" => 1, "caution" => 2, "high" => 3, "critical" => 4, _ => -1 };
    public static string Worst(params string[] states) => states.OrderByDescending(Rank).FirstOrDefault() ?? "unknown";
    public static string Label(string state) => state switch
    {
        "good" => "In range", "watch" => "Slight deviation", "caution" => "Caution",
        "high" => "High alert", "critical" => "Urgent review", _ => "Repeat reading"
    };
    public static string Pulse(int bpm, string quality) => bpm <= 0 || quality != "good" ? "unknown"
        : bpm is < 35 or > 160 ? "critical"
        : bpm is < 40 or > 130 ? "high"
        : bpm is < 50 or > 110 ? "caution"
        : bpm is < 60 or > 100 ? "watch" : "good";
    public static string Temperature(double value) => !double.IsFinite(value) ? "unknown"
        : value is < 10 or > 35 ? "critical"
        : value is < 14 or > 32 ? "high"
        : value is < 16 or > 29 ? "caution"
        : value is < 18 or > 27 ? "watch" : "good";
    public static string Humidity(double value) => !double.IsFinite(value) || value is < 0 or > 100 ? "unknown"
        : value is < 10 or > 90 ? "critical"
        : value is < 15 or > 85 ? "high"
        : value is < 20 or > 80 ? "caution"
        : value is < 25 or > 75 ? "watch" : "good";
}
