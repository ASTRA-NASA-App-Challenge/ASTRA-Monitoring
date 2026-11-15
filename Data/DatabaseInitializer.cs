using Microsoft.EntityFrameworkCore;

namespace MonitoringApi.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(ApiDbContext db, CancellationToken ct = default)
    {
        // Adopt the previous EnsureCreated PIN schema only after verifying its shape.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='CrewMembers'";
            var legacy = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
            if (legacy && !(await db.Database.GetAppliedMigrationsAsync(ct)).Any())
            {
                foreach (var table in new[] { "CrewMembers", "Devices", "Measurements", "EnvironmentReadings" })
                {
                    cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
                    var columns = new HashSet<string>();
                    await using (var reader = await cmd.ExecuteReaderAsync(ct))
                        while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(1));
                    var required = table switch
                    {
                        "CrewMembers" => new[] { "Pin" },
                        "Devices" => new[] { "DeviceId", "CredentialHash", "Kind" },
                        "Measurements" => new[] { "Id", "Pin", "ClientMeasurementId", "PhotoPath" },
                        _ => new[] { "Id", "Pin", "ClientReadingId", "MeasuredAt" }
                    };
                    if (required.Any(x => !columns.Contains(x)))
                        throw new InvalidOperationException("The existing SQLite schema is not the PIN schema. Back it up and migrate it explicitly.");
                }
                var initial = db.Database.GetMigrations().First();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ({initial}, {"10.0.15"})", ct);
                await tx.CommitAsync(ct);
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
        await db.Database.MigrateAsync(ct);
    }
}
