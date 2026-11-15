using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MonitoringApi.Data;
using MonitoringApi.Services;

namespace MonitoringApi.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string WebKey = "test-web-key-012345678901234567890123456789";
    public const string UnoKey = "test-uno-key-012345678901234567890123456789";
    public const string CameraKey = "test-camera-key-012345678901234567890123456789";
    public readonly DirectoryInfo Directory = System.IO.Directory.CreateTempSubdirectory("astra-api-tests-");
    public readonly StorageStub Storage = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = $"Data Source={Path.Combine(Directory.FullName, "test.db")};Foreign Keys=True;Pooling=False",
            ["MonitoringApi:WebApiKey"] = WebKey,
            ["Supabase:Url"] = "https://storage.test/",
            ["Supabase:ServiceRoleKey"] = "test-storage-secret",
            ["Supabase:Bucket"] = "photos",
            ["MonitoringApi:Devices:0:DeviceId"] = "uno-test", ["MonitoringApi:Devices:0:Kind"] = "uno",
            ["MonitoringApi:Devices:0:Credential"] = UnoKey, ["MonitoringApi:Devices:0:PairedDeviceId"] = "cam-test",
            ["MonitoringApi:Devices:1:DeviceId"] = "cam-test", ["MonitoringApi:Devices:1:Kind"] = "camera",
            ["MonitoringApi:Devices:1:Credential"] = CameraKey, ["MonitoringApi:Devices:1:PairedDeviceId"] = "uno-test",
            ["Logging:LogLevel:Default"] = "Warning"
        }));
        builder.ConfigureServices(services =>
        {
            // Minimal-host configuration callbacks can run after the host selects its connection.
            // Override the context registration itself so tests can never use the application database.
            services.RemoveAll<DbContextOptions<ApiDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApiDbContext>>();
            services.AddDbContext<ApiDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(Directory.FullName, "test.db")};Foreign Keys=True;Pooling=False"));
            foreach (var service in services.Where(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(PhotoCleanupWorker)).ToArray()) services.Remove(service);
            services.AddHttpClient("supabase").ConfigurePrimaryHttpMessageHandler(() => Storage);
        });
    }
    public HttpClient Admin()
    {
        var client = CreateClient();
        using (var scope = Services.CreateScope())
        {
            var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(scope.ServiceProvider.GetRequiredService<ApiDbContext>().Database.GetConnectionString());
            Assert.StartsWith(Directory.FullName, connection.DataSource);
        }
        client.DefaultRequestHeaders.Add("X-Api-Key", WebKey); return client;
    }
    public HttpClient Uno()
    {
        var client = CreateClient(); client.DefaultRequestHeaders.Add("X-Device-Id", "uno-test");
        client.DefaultRequestHeaders.Add("X-Device-Key", UnoKey); return client;
    }
    public async Task<T> Db<T>(Func<ApiDbContext, Task<T>> read)
    {
        await using var scope = Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<ApiDbContext>());
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && System.IO.Directory.Exists(Directory.FullName))
        { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(true); }
    }
}

public sealed class StorageStub : HttpMessageHandler
{
    public readonly ConcurrentDictionary<string, byte[]> Objects = new();
    public bool FailUploads;
    public bool FailAfterStore;
    public bool FailDeletes;
    public readonly TaskCompletionSource<bool> UploadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool>? UploadGate;
    public readonly ConcurrentQueue<string> Deleted = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Equal("storage.test", request.RequestUri!.Host);
        Assert.Equal("Bearer test-storage-secret", request.Headers.Authorization!.ToString());
        var path = request.RequestUri.AbsolutePath;
        if (path == "/storage/v1/object/list/photos")
        {
            if (FailDeletes) return new(HttpStatusCode.ServiceUnavailable);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var prefix = json.RootElement.GetProperty("prefix").GetString()!;
            var names = Objects.Keys.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).Take(1000).Select(x => new { name = x[prefix.Length..] });
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(names) };
        }
        if (request.Method == HttpMethod.Delete)
        {
            if (FailDeletes) return new(HttpStatusCode.ServiceUnavailable);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            foreach (var value in json.RootElement.GetProperty("prefixes").EnumerateArray())
            { var name = value.GetString()!; Objects.TryRemove(name, out _); Deleted.Enqueue(name); }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) };
        }
        const string upload = "/storage/v1/object/photos/";
        const string download = "/storage/v1/object/authenticated/photos/";
        if (request.Method == HttpMethod.Post && path.StartsWith(upload))
        {
            if (FailUploads) return new(HttpStatusCode.ServiceUnavailable);
            UploadStarted.TrySetResult(true);
            if (UploadGate is not null) await UploadGate.Task.WaitAsync(ct);
            Objects[path[upload.Length..]] = await request.Content!.ReadAsByteArrayAsync(ct);
            if (FailAfterStore) throw new HttpRequestException("Simulated lost response after storage accepted the photo.");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) };
        }
        if (request.Method == HttpMethod.Get && path.StartsWith(download) && Objects.TryGetValue(path[download.Length..], out var bytes))
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new("image/jpeg"); return response;
        }
        return new(HttpStatusCode.NotFound);
    }
}
