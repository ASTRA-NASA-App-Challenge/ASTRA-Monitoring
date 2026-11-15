using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Astra.Demo;

public sealed class DemoApi : IDisposable
{
    private readonly HttpClient http;
    public DemoApi(Uri address, string key)
    {
        http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.Add("X-Api-Key", key);
    }
    public async Task<T> JsonAsync<T>(HttpMethod method, string path, object? body = null)
    {
        var bytes = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, DemoRunner.Json);
        var result = await SendAsync(method, path, bytes, "application/json");
        return JsonSerializer.Deserialize<T>(result, DemoRunner.Json) ?? throw new InvalidOperationException("Empty API JSON response.");
    }
    public async Task UploadAsync(string pin, string measurementId, byte[] jpeg) =>
        _ = await SendAsync(HttpMethod.Put, $"api/v1/crew/{pin}/measurements/{measurementId}/photo", jpeg, "image/jpeg");
    public Task<byte[]> PhotoAsync(string pin, string measurementId) => SendAsync(HttpMethod.Get, $"api/v1/crew/{pin}/measurements/{measurementId}/photo", null, null);
    public async Task<bool> AnonymousPhotoIsDeniedAsync(string pin, string id)
    {
        using var client = new HttpClient { BaseAddress = http.BaseAddress };
        using var response = await client.GetAsync($"api/v1/crew/{pin}/measurements/{id}/photo");
        return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    }
    private async Task<byte[]> SendAsync(HttpMethod method, string path, byte[]? body, string? type)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            // History is batched, so a full seed now fits below the existing request
            // limit. Honor 429 responses rather than adding a delay to every request.
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(type!);
            }
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            { await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5)); continue; }
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Authenticated API {method} {path} returned {(int)response.StatusCode}; no upload state was bypassed.");
            if (type is null && response.Content.Headers.ContentType?.MediaType != "image/jpeg")
                throw new InvalidOperationException("Photo GET did not return image/jpeg.");
            return bytes;
        }
        throw new TimeoutException("API rate limiting did not clear.");
    }
    public void Dispose() => http.Dispose();
}

public sealed record ApiPage<T>(List<T> Items, string? NextCursor);
public sealed record HistoryPulse(string MeasurementId, DateTimeOffset MeasuredAt, MonitoringApi.Models.PulseSummary Pulse, string? PhotoUrl);
