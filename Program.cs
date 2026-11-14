using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MonitoringApi;
using MonitoringApi.Auth;
using MonitoringApi.Data;
using MonitoringApi.Identity;
using MonitoringApi.Services;
using Npgsql.EntityFrameworkCore.PostgreSQL;

var builder = WebApplication.CreateBuilder(args);
var identityConnection = ResolveConnection(builder, "DefaultConnection", "Host=localhost;Port=5432;Database=astra;Username=postgres;Password=postgres");
var monitoringConnection = ResolveConnection(builder, "MonitoringApi:ConnectionString", "Host=localhost;Port=5432;Database=astra_monitoring;Username=postgres;Password=postgres");
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(identityConnection));
builder.Services.AddDbContext<ApiDbContext>(options => options.UseNpgsql(monitoringConnection));

builder.Services.AddHttpClient();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Password.RequiredLength = 6;
        options.Password.RequireUppercase = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireNonAlphanumeric = false;
    })
    .AddRoles<IdentityRole>()
    .AddSignInManager()
    .AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.AddScoped<IPasswordHasher<DeviceRegistration>, PasswordHasher<DeviceRegistration>>();
builder.Services.AddScoped<ApiDeviceProvisioner>();
builder.Services.AddScoped<DemoDataSeeder>();
builder.Services.AddScoped<SupabasePhotoStorage>();
builder.Services.AddAuthorization(options => options.AddPolicy(MedicalPolicies.ReadRecord,
    policy => policy.RequireAuthenticatedUser().AddRequirements(new ReadMedicalRecordRequirement())));
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, MedicalRecordAuthorizationHandler>();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = ApiSchemes.Bearer;
        options.DefaultChallengeScheme = ApiSchemes.Bearer;
    })
    .AddScheme<AuthenticationSchemeOptions, BearerAuthenticationHandler>(ApiSchemes.Bearer, _ => { })
    .AddScheme<AuthenticationSchemeOptions, DeviceAuthenticationHandler>(ApiSchemes.Device, _ => { });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        $"{context.Connection.RemoteIpAddress}:{context.Request.Headers["X-Device-Id"]}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 }));
    options.AddPolicy("device-read", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Request.Headers["X-Device-Id"].ToString(),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.Configure<ApiBehaviorOptions>(options => options.InvalidModelStateResponseFactory = ApiValidationResponseFactory.Create);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 5 * 1024 * 1024);
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var identityDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await EnsureIdentityDatabaseAsync(identityDb);
    var services = scope.ServiceProvider;
    var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var role in AppRoles.All)
        if (!await roleManager.RoleExistsAsync(role))
        {
            var result = await roleManager.CreateAsync(new IdentityRole(role));
            if (!result.Succeeded) throw new InvalidOperationException("Could not initialize API Identity roles.");
        }

    var apiDb = services.GetRequiredService<ApiDbContext>();
    await apiDb.Database.EnsureCreatedAsync();
    await services.GetRequiredService<ApiDeviceProvisioner>().ProvisionAsync(CancellationToken.None);

    if (args.Contains("--seed-demo", StringComparer.OrdinalIgnoreCase))
    {
        if (!app.Environment.IsDevelopment())
            throw new InvalidOperationException("Demo data can only be seeded in the Development environment.");
        var credentialFile = await services.GetRequiredService<DemoDataSeeder>().SeedAsync(builder.Environment.ContentRootPath);
        Console.WriteLine($"Synthetic demo data created. Local credentials were written to {credentialFile}.");
        return;
    }
}
app.UseMiddleware<ApiErrorMiddleware>();
//app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

static string ResolveConnection(WebApplicationBuilder builder, string key, string fallback)
{
    var value = builder.Configuration.GetConnectionString(key) ?? builder.Configuration[key] ?? Environment.GetEnvironmentVariable(key) ?? fallback;
    if (string.IsNullOrWhiteSpace(value)) return fallback;
    return value.Trim();
}

static async Task EnsureIdentityDatabaseAsync(ApplicationDbContext db)
{
    await db.Database.OpenConnectionAsync();
    try
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ('AspNetUsers', 'AspNetRoles')";
        var tableCount = Convert.ToInt32(await command.ExecuteScalarAsync());
        if (tableCount != 2)
            throw new InvalidOperationException("The shared Identity database is not initialized. Start ASTRA-Web once and point DefaultConnection to the Supabase PostgreSQL database.");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}
