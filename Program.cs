using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitoringApi;
using MonitoringApi.Data;
using MonitoringApi.Services;

var builder = WebApplication.CreateBuilder(args);
DotEnvConfiguration.Add(builder.Configuration, builder.Environment.ContentRootPath, args);
var connection = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=monitoring.db;Foreign Keys=True";
builder.Services.AddDbContext<ApiDbContext>(o => o.UseSqlite(connection));
builder.Services.AddHttpClient();
builder.Services.AddScoped<SupabasePhotoStorage>();
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = ApiValidationResponseFactory.Create);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 5 * 1024 * 1024);
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
    await db.Database.EnsureCreatedAsync();
    if (builder.Configuration["seed-pin"] is { } seedPin)
    {
        if (!app.Environment.IsDevelopment())
            throw new InvalidOperationException("Sample data can only be seeded in Development.");
        await PinSampleSeeder.SeedAsync(db, seedPin);
        return;
    }
    foreach (var item in builder.Configuration.GetSection("MonitoringApi:Devices").GetChildren())
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
    }
    await db.SaveChangesAsync();
}
app.UseMiddleware<ApiErrorMiddleware>();
app.MapControllers();
app.Run();

