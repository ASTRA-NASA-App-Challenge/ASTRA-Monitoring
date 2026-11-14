using Microsoft.EntityFrameworkCore;

namespace MonitoringApi.Data;

public sealed class ApiDbContext(DbContextOptions<ApiDbContext> options) : DbContext(options)
{
    public DbSet<DeviceRegistration> Devices => Set<DeviceRegistration>();
    public DbSet<ApiSession> Sessions => Set<ApiSession>();
    public DbSet<Measurement> Measurements => Set<Measurement>();
    public DbSet<EnvironmentReading> EnvironmentReadings => Set<EnvironmentReading>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceRegistration>().HasKey(x => x.DeviceId);
        modelBuilder.Entity<DeviceRegistration>().Property(x => x.CredentialHash).IsRequired();
        modelBuilder.Entity<ApiSession>().HasKey(x => x.Id);
        modelBuilder.Entity<ApiSession>().HasIndex(x => x.AccessTokenHash).IsUnique();
        modelBuilder.Entity<ApiSession>().HasIndex(x => x.RefreshTokenHash).IsUnique();
        modelBuilder.Entity<ApiSession>().Property(x => x.AccessExpiresAt).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        modelBuilder.Entity<ApiSession>().Property(x => x.RefreshExpiresAt).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        modelBuilder.Entity<ApiSession>().Property(x => x.CreatedAt).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        modelBuilder.Entity<ApiSession>().Property(x => x.RevokedAt).HasConversion(x => x.HasValue ? x.Value.UtcTicks : (long?)null,
            x => x.HasValue ? new DateTimeOffset(x.Value, TimeSpan.Zero) : null);
        modelBuilder.Entity<Measurement>().HasKey(x => x.Id);
        modelBuilder.Entity<Measurement>().HasIndex(x => new { x.UnoDeviceId, x.IdempotencyKey }).IsUnique();
        modelBuilder.Entity<Measurement>().HasIndex(x => new { x.AstronautId, x.MeasuredAt });
        modelBuilder.Entity<Measurement>().Property(x => x.MeasuredAt).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        modelBuilder.Entity<Measurement>().Property(x => x.CreatedAt).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        modelBuilder.Entity<Measurement>().Property(x => x.PhotoExpiresAt).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        modelBuilder.Entity<Measurement>().Property(x => x.PhotoReceivedAt).HasConversion(x => x.HasValue ? x.Value.UtcTicks : (long?)null,
            x => x.HasValue ? new DateTimeOffset(x.Value, TimeSpan.Zero) : null);
        modelBuilder.Entity<EnvironmentReading>().HasKey(x => x.Id);
        modelBuilder.Entity<EnvironmentReading>().HasIndex(x => new { x.SessionId, x.IdempotencyKey }).IsUnique();
        modelBuilder.Entity<EnvironmentReading>().HasIndex(x => new { x.AstronautId, x.MeasuredAt });
        modelBuilder.Entity<EnvironmentReading>().Property(x => x.MeasuredAt).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        modelBuilder.Entity<EnvironmentReading>().Property(x => x.ReceivedAt).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
    }
}

public sealed class DeviceRegistration
{
    public required string DeviceId { get; set; }
    public required string Kind { get; set; } // uno or camera
    public required string CredentialHash { get; set; }
    public string? PairedDeviceId { get; set; }
    public string AstronautIdsJson { get; set; } = "[]";
    public bool Active { get; set; } = true;
}

public sealed class ApiSession
{
    public required string Id { get; set; }
    public required string AstronautId { get; set; }
    public required string UnoDeviceId { get; set; }
    public required string AccessTokenHash { get; set; }
    public DateTimeOffset AccessExpiresAt { get; set; }
    public required string RefreshTokenHash { get; set; }
    public DateTimeOffset RefreshExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class Measurement
{
    public required string Id { get; set; }
    public required string ClientMeasurementId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string AstronautId { get; set; }
    public required string SessionId { get; set; }
    public required string UnoDeviceId { get; set; }
    public required string CameraDeviceId { get; set; }
    public DateTimeOffset MeasuredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int Bpm { get; set; }
    public required string Quality { get; set; }
    public required string AlgorithmVersion { get; set; }
    public string? PulseStatusReason { get; set; }
    public string? WaveformJson { get; set; }
    public required string MediaType { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string PhotoStatus { get; set; } = "awaiting_upload";
    public DateTimeOffset PhotoExpiresAt { get; set; }
    public string? PhotoPath { get; set; }
    public string? PhotoSha256 { get; set; }
    public string? PhotoIdempotencyKey { get; set; }
    public DateTimeOffset? PhotoReceivedAt { get; set; }
}

public sealed class EnvironmentReading
{
    public required string Id { get; set; }
    public required string ClientReadingId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string SessionId { get; set; }
    public required string AstronautId { get; set; }
    public required string DeviceId { get; set; }
    public DateTimeOffset MeasuredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public double TemperatureC { get; set; }
    public double RelativeHumidityPercent { get; set; }
}
