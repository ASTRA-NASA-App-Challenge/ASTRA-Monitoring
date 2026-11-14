using System.Net.Http.Headers;

namespace MonitoringApi.Services;

public sealed class SupabasePhotoStorage(IConfiguration configuration, ILogger<SupabasePhotoStorage> logger, IHttpClientFactory httpClientFactory)
{
    private readonly string? _url = configuration["Supabase:Url"];
    private readonly string? _serviceRoleKey = configuration["Supabase:ServiceRoleKey"];

    public async Task<string> UploadAsync(Stream imageStream, string fileName, string contentType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_url) || string.IsNullOrWhiteSpace(_serviceRoleKey))
            throw new InvalidOperationException("Supabase URL and ServiceRoleKey must be configured.");

        var bucket = configuration["Supabase:Bucket"] ?? "photos";
        var path = $"{DateTimeOffset.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}_{fileName}";
        var url = $"{_url.TrimEnd('/')}/storage/v1/object/{bucket}/{path}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        using var content = new StreamContent(imageStream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceRoleKey);
        request.Content = content;

        using var client = httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("Supabase upload failed for {FileName}: {StatusCode} {Body}", fileName, response.StatusCode, body);
            throw new InvalidOperationException($"Supabase upload failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return $"{_url.TrimEnd('/')}/storage/v1/object/public/{bucket}/{path}";
    }

    public async Task<Stream> DownloadAsync(string publicUrl, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();
        var response = await client.GetAsync(publicUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }
}
