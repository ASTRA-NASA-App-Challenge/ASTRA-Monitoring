using MonitoringApi.Data;
using Common.Models;

namespace Astra.Demo;

public sealed record Scenario(string Pin, int Bpm, double Temperature, double Humidity, string State, int AgeMinutes);
public sealed record DemoSample(string Pin, int Slot, DateTimeOffset At, int Bpm, string Quality, string? Reason,
    double Temperature, double Humidity, bool HasPhoto)
{
    public string MeasurementClientId => DemoSeedContract.ClientId("pulse", Pin, Slot);
    public string EnvironmentClientId => DemoSeedContract.ClientId("cabin", Pin, Slot);
}

public static class DemoSeries
{
    public const string Algorithm = DemoSeedContract.Algorithm;
    public const int SampleCount = DemoSeedContract.SampleCount; // 124 six-hour intervals cover exactly 31 days.
    public static readonly Scenario[] Scenarios =
    [
        new("410201", 72, 22, 45, "good", 15),
        new("410202", 105, 22, 45, "watch", 15),
        new("520301", 185, 38, 94, "critical", 15),
        new("520302", 120, 30, 83, "caution", 15),
        new("520303", 145, 34, 88, "high", 15),
        new("520304", 0, 22, 45, "unknown", 420)
    ];

    public static IEnumerable<DemoSample> Samples(DateTimeOffset presentation)
    {
        foreach (var s in Scenarios)
        for (var i = 0; i < SampleCount; i++)
        {
            var at = presentation.AddMinutes(-s.AgeMinutes).AddHours(-6 * i);
            if (i == 0)
            {
                yield return new(s.Pin, i, at, s.Bpm, s.Bpm == 0 ? "poor" : "good",
                    s.Bpm == 0 ? "Synthetic demo: pulse unavailable because the optical sensor lost skin contact; repeat check-in is overdue."
                        : "Synthetic presentation scenario, not a clinical measurement.", s.Temperature, s.Humidity, true);
                continue;
            }
            var progress = Math.Max(0, (120.0 - i) / 120);
            var wave = Math.Sin(i * 0.53) * 4 + Math.Cos(i * 0.21) * 2;
            var profile = Array.FindIndex(Scenarios, x => x.Pin == s.Pin);
            var bpm = profile switch
            {
                0 => 72 + wave, // Stable baseline, short exertion and recovery.
                1 => 76 + 25 * progress + wave,
                2 => 78 + 65 * progress + wave,
                3 => 78 + 25 * progress + 9 * Math.Sin(i * 0.18) + wave,
                4 => 70 + 55 * progress + wave,
                _ => 71 + wave
            };
            var recovery = i % 29 is >= 19 and <= 23;
            if (recovery) bpm = 72 + wave;
            if (i % 41 == 8) bpm += profile == 0 ? 38 : 32;
            if (i % 47 == 17) bpm = 48 + profile;
            var unavailable = i % 53 == 12;
            var quality = unavailable ? "poor" : i % 23 == 9 ? "fair" : i % 31 == 14 ? "poor" : "good";
            var temp = profile switch { 2 => 22 + 12 * progress, 3 => 22 + 6 * progress, 4 => 22 + 9 * progress, _ => 22.0 };
            var humidity = profile switch { 2 => 45 + 39 * progress, 3 => 45 + 31 * progress, 4 => 45 + 36 * progress, _ => 45.0 };
            if (recovery) { temp = 22; humidity = 45; }
            temp += Math.Sin(i * 0.32) * 0.7;
            humidity += Math.Cos(i * 0.28) * 3;
            yield return new(s.Pin, i, at, unavailable ? 0 : (int)Math.Round(bpm), quality,
                unavailable ? "Synthetic demo: no stable optical pulse signal; motion or contact interruption."
                    : quality != "good" ? "Synthetic demo: reduced optical signal reliability; repeat the measurement."
                    : recovery ? "Synthetic demo: recovery period following an earlier deviation."
                    : "Synthetic demo trend with scheduled variation and occasional outliers.",
                Math.Round(temp, 1), Math.Round(humidity, 1), i is 20 or 76);
        }
    }

    public static string Severity(int bpm, string quality, double temp, double humidity, DateTimeOffset at, DateTimeOffset now)
    {
        var pulse = HealthSeverity.Pulse(bpm, quality);
        var worst = HealthSeverity.Worst(pulse, HealthSeverity.Temperature(temp), HealthSeverity.Humidity(humidity));
        if (HealthSeverity.Rank(worst) > 0) return worst;
        return pulse == "unknown" || now - at >= TimeSpan.FromHours(6) ? "unknown" : "good";
    }
}
