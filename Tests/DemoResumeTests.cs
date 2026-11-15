using Astra.Demo;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MonitoringApi.Data;

namespace MonitoringApi.Tests;

public class DemoResumeTests
{
    [Fact]
    public async Task EmptyDatabaseUsesCurrentTimeWithoutPersistingDemoState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApiDbContext(new DbContextOptionsBuilder<ApiDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var before = DateTimeOffset.UtcNow;
        var state = await DemoState.FromDatabaseAsync(db);
        Assert.InRange(state.Presentation, before, DateTimeOffset.UtcNow);
        Assert.True(state.IsNew);
        Assert.Null(state.UnoId);
        Assert.Empty(await db.Devices.ToListAsync());
        Assert.Empty(await db.CrewMembers.ToListAsync());
    }

    [Fact]
    public async Task PartialHistoryResumesFromItsActualTimestampAndKeepsStableIds()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApiDbContext(new DbContextOptionsBuilder<ApiDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var presentation = DateTimeOffset.UtcNow.AddDays(-2);
        var sample = DemoSeries.Samples(presentation).Single(x => x.Pin == "520304" && x.Slot == 76);
        db.CrewMembers.Add(new CrewMember { Pin = sample.Pin });
        db.Measurements.Add(new Measurement { Id = "demo", Pin = sample.Pin,
            ClientMeasurementId = sample.MeasurementClientId, MeasuredAt = sample.At,
            UnoDeviceId = "isolated-uno", CameraDeviceId = "isolated-camera", Quality = sample.Quality,
            AlgorithmVersion = DemoSeries.Algorithm, MediaType = "image/jpeg" });
        await db.SaveChangesAsync();
        var state = await DemoState.FromDatabaseAsync(db);
        Assert.False(state.IsNew);
        Assert.Equal(presentation, state.Presentation);
        Assert.Equal("isolated-uno", state.UnoId);
        Assert.Equal("isolated-camera", state.CameraId);
        Assert.Equal(sample, DemoSeries.Samples(state.Presentation).Single(x => x.MeasurementClientId == sample.MeasurementClientId));
        Assert.Single(await db.Measurements.ToListAsync());
    }

    [Fact]
    public async Task CabinOnlyPartialHistoryCanResumeWithoutASidecarFile()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApiDbContext(new DbContextOptionsBuilder<ApiDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var presentation = DateTimeOffset.UtcNow.AddDays(-1);
        var sample = DemoSeries.Samples(presentation).Single(x => x.Pin == "410201" && x.Slot == 20);
        db.CrewMembers.Add(new CrewMember { Pin = sample.Pin });
        db.EnvironmentReadings.Add(new EnvironmentReading { Id = "cabin", Pin = sample.Pin,
            ClientReadingId = sample.EnvironmentClientId, DeviceId = "uno", MeasuredAt = sample.At });
        await db.SaveChangesAsync();
        var state = await DemoState.FromDatabaseAsync(db);
        Assert.False(state.IsNew);
        Assert.Equal(presentation, state.Presentation);
        Assert.Single(await db.EnvironmentReadings.ToListAsync());
    }
}
