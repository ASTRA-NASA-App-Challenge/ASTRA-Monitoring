using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;

namespace MonitoringApi.Identity;

// Maps the existing Web Identity user table so this API can validate PINs without
// depending on, or changing, the web application's project assembly.
public sealed class ApplicationUser : IdentityUser
{
    public bool IsGroundControl { get; set; }
    public string? PinLookup { get; set; }
    [JsonIgnore]
    public string? ProtectedPin { get; set; }
}
