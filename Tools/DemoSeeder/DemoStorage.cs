using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MonitoringApi.Services;

namespace Astra.Demo;

public sealed class DemoStorage(IConfiguration config)
{
    public async Task VerifyPrivateAsync()
    {
        var url = config["Supabase:Url"] ?? throw new InvalidOperationException("Supabase URL is missing.");
        var key = config["Supabase:ServiceRoleKey"] ?? throw new InvalidOperationException("Supabase service credential is missing.");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await client.GetAsync($"{url.TrimEnd('/')}/storage/v1/bucket/{Uri.EscapeDataString(config["Supabase:Bucket"] ?? "photos")}");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Could not verify the configured private Supabase bucket.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!json.RootElement.TryGetProperty("public", out var isPublic) || isPublic.ValueKind != JsonValueKind.False)
            throw new InvalidOperationException("The configured bucket must already be private; the seeder will not change its access policy.");
        Console.WriteLine("Configured Supabase bucket verified private; no storage policy was changed.");
    }
    public Task RemoveIncompleteObjectAsync(string pin, string measurementId) =>
        new SupabasePhotoStorage(config, NullLogger<SupabasePhotoStorage>.Instance, new Factory()).DeleteObjectAsync(pin, measurementId, default);
    private sealed class Factory : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(20) }; }
}
