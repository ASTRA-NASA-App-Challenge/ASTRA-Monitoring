using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitoringApi;
using MonitoringApi.Data;
using MonitoringApi.Services;
using MonitoringApi.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
DotEnvConfiguration.Add(builder.Configuration, builder.Environment.ContentRootPath, args);
var connection = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=monitoring.db;Foreign Keys=True";
var sqlite = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection) { ForeignKeys = true };
if (!Path.IsPathRooted(sqlite.DataSource) && sqlite.DataSource != ":memory:")
    sqlite.DataSource = Path.Combine(builder.Environment.ContentRootPath, sqlite.DataSource);
connection = sqlite.ToString();
builder.Services.AddDbContext<ApiDbContext>(o => o.UseSqlite(connection));
builder.Services.AddHttpClient("supabase", client => client.Timeout = TimeSpan.FromSeconds(20)).RemoveAllLoggers();
builder.Services.AddScoped<SupabasePhotoStorage>();
builder.Services.AddScoped<CrewDeletion>();
builder.Services.AddScoped<PhotoOrphanCleanup>();
builder.Services.AddHostedService<PhotoCleanupWorker>();
builder.Services.AddSingleton<IPasswordHasher<DeviceRegistration>, PasswordHasher<DeviceRegistration>>();
builder.Services.AddAuthentication(ApiAccess.SchemeName).AddScheme<AuthenticationSchemeOptions, ApiAccess>(ApiAccess.SchemeName, _ => { });
builder.Services.AddAuthorization(o => o.AddPolicy("web", p => p.RequireAuthenticatedUser().RequireClaim("caller", "web")));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 20, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddControllers(o => o.Filters.Add<ApiErrorResultFilter>()).AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = ApiValidationResponseFactory.Create);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 5 * 1024 * 1024);
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
    await DatabaseInitializer.InitializeAsync(db);
    if (builder.Configuration["seed-pin"] is { } seedPin)
    {
        if (!app.Environment.IsDevelopment())
            throw new InvalidOperationException("Sample data can only be seeded in Development.");
        await PinSampleSeeder.SeedAsync(db, seedPin);
        return;
    }
    if (builder.Configuration["MonitoringApi:WebApiKey"] is not { Length: >= 32 and <= 512 })
        throw new InvalidOperationException("Set MonitoringApi:WebApiKey to a secret of 32 to 512 characters.");
    var configuredDevices = builder.Configuration.GetSection("MonitoringApi:Devices").GetChildren().ToArray();
    var configuredIds = configuredDevices.Select(x => x["DeviceId"]).ToHashSet();
    foreach (var old in await db.Devices.ToListAsync())
        if (!configuredIds.Contains(old.DeviceId)) old.Active = false;
    foreach (var item in configuredDevices)
    {
        var id = item["DeviceId"];
        var kind = item["Kind"];
        if (string.IsNullOrWhiteSpace(id) || kind is not ("uno" or "camera"))
            throw new InvalidOperationException("Configured devices require DeviceId and Kind (uno or camera).");
        var device = await db.Devices.FindAsync(id);
        if (device is null)
        {
            device = new DeviceRegistration { DeviceId = id, Kind = kind, CredentialHash = "" };
            db.Devices.Add(device);
        }
        device.Kind = kind;
        device.PairedDeviceId = item["PairedDeviceId"];
        device.Active = true;
        var credential = item["Credential"];
        if (credential is not { Length: >= 16 and <= 512 })
            throw new InvalidOperationException("Each device needs a Credential of 16 to 512 characters matching its firmware configuration.");
        device.CredentialHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<DeviceRegistration>>().HashPassword(device, credential);
    }
    await db.SaveChangesAsync();
}
app.UseMiddleware<ApiErrorMiddleware>();
app.UseStatusCodePages(async ctx => await ApiErrors.WriteAsync(ctx.HttpContext));
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

public partial class Program { }
