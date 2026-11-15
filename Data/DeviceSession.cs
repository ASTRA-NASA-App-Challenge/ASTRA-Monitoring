namespace MonitoringApi.Data;

// Only the PIN refers to a crew identity; all other identifiers describe a device session.
public sealed class DeviceSession
{
    public required string Id { get; set; }
    public required string Pin { get; set; }
    public required string DeviceId { get; set; }
    public required string AccessHash { get; set; }
    public required string RefreshHash { get; set; }
    public long AccessExpiresTicks { get; set; }
    public long RefreshExpiresTicks { get; set; }
}
