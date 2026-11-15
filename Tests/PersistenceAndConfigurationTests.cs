using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using MonitoringApi.Data;

namespace MonitoringApi.Tests;

public class PersistenceAndConfigurationTests
{
    [Fact]
    public async Task ExistingEnsureCreatedDatabaseIsAdoptedWithoutLosingRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApiDbContext>().UseSqlite(connection).Options;
        await using var db = new ApiDbContext(options);
        var first = db.Database.GetMigrations().First();
        await db.GetService<IMigrator>().MigrateAsync(first);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO CrewMembers (Pin) VALUES ('012345')");
        db.EnvironmentReadings.Add(new EnvironmentReading
        {
            Id = "env_existing", Pin = "012345", ClientReadingId = "old-client-id", DeviceId = "old-uno",
            MeasuredAt = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow, TemperatureC = 22, RelativeHumidityPercent = 45
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE __EFMigrationsHistory");
        db.ChangeTracker.Clear();
        await DatabaseInitializer.InitializeAsync(db);
        Assert.Equal("env_existing", (await db.EnvironmentReadings.SingleAsync()).Id);
        Assert.False((await db.CrewMembers.SingleAsync()).Retired);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        await DatabaseInitializer.InitializeAsync(db);
        Assert.Single(await db.EnvironmentReadings.ToListAsync());
    }

    [Fact]
    public async Task ForeignKeysAndUniqueReadingKeysAreEnforcedBySqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        await using var db = new ApiDbContext(new DbContextOptionsBuilder<ApiDbContext>().UseSqlite(connection).Options);
        await DatabaseInitializer.InitializeAsync(db);
        EnvironmentReading Reading(string id) => new() { Id = id, Pin = "012345", ClientReadingId = "same-client", DeviceId = "uno", MeasuredAt = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow };
        db.EnvironmentReadings.Add(Reading("first"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        db.CrewMembers.Add(new CrewMember { Pin = "012345" }); await db.SaveChangesAsync();
        db.EnvironmentReadings.Add(Reading("first")); await db.SaveChangesAsync();
        db.EnvironmentReadings.Add(Reading("duplicate"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void DotEnvOverridesAppsettingsButKeepsHigherPriorityProviders()
    {
        var dir = Directory.CreateTempSubdirectory("astra-dotenv-tests-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "appsettings.json"), """{"ConnectionStrings":{"DefaultConnection":"Data Source=default.db"},"Shared":"default"}""");
            File.WriteAllText(Path.Combine(dir.FullName, ".env"), "ConnectionStrings__DefaultConnection=\"Data Source=local.db\"\nShared=local\nQuoted='value=with=equals'\n");
            using var config = new ConfigurationManager(); config.AddJsonFile(Path.Combine(dir.FullName, "appsettings.json"));
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Shared"] = "user-secret" });
            DotEnvConfiguration.Add(config, dir.FullName, []);
            Assert.Equal("Data Source=local.db", config.GetConnectionString("DefaultConnection"));
            Assert.Equal("user-secret", config["Shared"]); Assert.Equal("value=with=equals", config["Quoted"]);
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = "Data Source=deployment.db" });
            Assert.Equal("Data Source=deployment.db", config.GetConnectionString("DefaultConnection"));
        }
        finally { dir.Delete(true); }
    }
}
