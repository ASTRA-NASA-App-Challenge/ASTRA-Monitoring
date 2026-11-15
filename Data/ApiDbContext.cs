using Microsoft.EntityFrameworkCore;

namespace MonitoringApi.Data;

public sealed class ApiDbContext(DbContextOptions<ApiDbContext> options) : DbContext(options)
{
    public DbSet<DeviceRegistration> Devices => Set<DeviceRegistration>();
    public DbSet<CrewMember> CrewMembers => Set<CrewMember>();
    public DbSet<Measurement> Measurements => Set<Measurement>();
    public DbSet<EnvironmentReading> EnvironmentReadings => Set<EnvironmentReading>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceRegistration>().HasKey(x => x.DeviceId);
        modelBuilder.Entity<DeviceRegistration>().Property(x => x.CredentialHash).IsRequired();
        modelBuilder.Entity<CrewMember>().HasKey(x => x.Pin);
        modelBuilder.Entity<Measurement>().HasOne<CrewMember>().WithMany().HasForeignKey(x => x.Pin).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<EnvironmentReading>().HasOne<CrewMember>().WithMany().HasForeignKey(x => x.Pin).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Measurement>().HasKey(x => x.Id);
        modelBuilder.Entity<Measurement>().HasIndex(x => new { AstronautId = x.Pin, x.MeasuredAt });
        modelBuilder.Entity<Measurement>().Property(x => x.MeasuredAt).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        modelBuilder.Entity<Measurement>().Property(x => x.CreatedAt).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        modelBuilder.Entity<Measurement>().Property(x => x.PhotoExpiresAt).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        modelBuilder.Entity<Measurement>().Property(x => x.PhotoReceivedAt).HasConversion(x => x.HasValue ? x.Value.UtcTicks : (long?)null,
            x => x.HasValue ? new DateTimeOffset(x.Value, TimeSpan.Zero) : null);
        modelBuilder.Entity<EnvironmentReading>().HasKey(x => x.Id);
        modelBuilder.Entity<EnvironmentReading>().HasIndex(x => new { AstronautId = x.Pin, x.MeasuredAt });
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

public sealed class CrewMember { public required string Pin { get; set; } public static bool IsValid(string? pin) => pin is { Length: 6 } && pin.All(c => c is >= '0' and <= '9'); }

public sealed class Measurement
{
    public required string Id { get; set; }
    public required string ClientMeasurementId { get; set; }
    public required string Pin { get; set; }

    public required string UnoDeviceId { get; set; }
    public required string CameraDeviceId { get; set; }
    public DateTimeOffset MeasuredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int Bpm { get; set; }
    
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString();
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
    public DateTimeOffset? PhotoReceivedAt { get; set; }
}

public sealed class EnvironmentReading
{
    public required string Id { get; set; }
    public required string ClientReadingId { get; set; }

    public required string Pin { get; set; }
    public required string DeviceId { get; set; }
    public DateTimeOffset MeasuredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public double TemperatureC { get; set; }
    
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString();
    public double RelativeHumidityPercent { get; set; }
}

