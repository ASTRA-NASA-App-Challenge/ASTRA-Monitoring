using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace MonitoringApi.Identity;

public static class AppRoles
{
    public const string Crew = "Crew";
    public const string Medic = "Medic";
    public const string GroundControl = "GroundControl";
    public static readonly string[] All = [Crew, Medic, GroundControl];
}

public static class MedicalPolicies
{
    public const string ReadRecord = "ReadMedicalRecord";
}

public sealed class ReadMedicalRecordRequirement : IAuthorizationRequirement;

public sealed class MedicalRecordAuthorizationHandler
    : AuthorizationHandler<ReadMedicalRecordRequirement, string>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
        ReadMedicalRecordRequirement requirement, string patientUserId)
    {
        if (context.User.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(patientUserId)
            && (context.User.IsInRole(AppRoles.Medic)
                || context.User.IsInRole(AppRoles.GroundControl)
                || (context.User.IsInRole(AppRoles.Crew)
                    && context.User.FindFirstValue(ClaimTypes.NameIdentifier) == patientUserId)))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
