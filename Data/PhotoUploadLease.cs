namespace MonitoringApi.Data;

// Short-lived coordination survives process crashes; no network I/O holds a SQLite write lock.
public sealed class PhotoUploadLease
{
    public required string MeasurementId { get; set; }
    public required string Pin { get; set; }
    public required string Token { get; set; }
    public long ExpiresTicks { get; set; }
}
