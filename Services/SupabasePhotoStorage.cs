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
        var path = fileName;
        var url = $"{_url.TrimEnd('/')}/storage/v1/object/{bucket}/{path}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        using var content = new StreamContent(imageStream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceRoleKey);
        request.Headers.Add("x-upsert", "true");
        request.Content = content;

        using var client = httpClientFactory.CreateClient("supabase");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("Supabase upload failed for {FileName}: {StatusCode} {Body}", fileName, response.StatusCode, body);
            throw new HttpRequestException("Supabase upload failed.", null, response.StatusCode);
        }

        return $"{_url.TrimEnd('/')}/storage/v1/object/public/{bucket}/{path}";
    }

    public async Task<Stream> DownloadAsync(string publicUrl, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("supabase");
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) || !Uri.TryCreate(_url, UriKind.Absolute, out var expected)
            || uri.Scheme != expected.Scheme || uri.Authority != expected.Authority
            || !uri.AbsolutePath.StartsWith("/storage/v1/object/public/", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid stored photo location.");
        var authenticatedUrl = publicUrl.Replace("/storage/v1/object/public/", "/storage/v1/object/authenticated/", StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Get, authenticatedUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceRoleKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        // Detach the returned stream from the HTTP response lifetime.
        return new MemoryStream(await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    public async Task DeleteFolderAsync(string pin, CancellationToken ct)
    {
        if (!MonitoringApi.Data.CrewMember.IsValid(pin)) throw new ArgumentException("Invalid PIN folder.");
        if (string.IsNullOrWhiteSpace(_url) || string.IsNullOrWhiteSpace(_serviceRoleKey))
            throw new InvalidOperationException("Supabase storage is not configured.");
        var bucket = Uri.EscapeDataString(configuration["Supabase:Bucket"] ?? "photos");
        using var client = httpClientFactory.CreateClient("supabase");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _serviceRoleKey);
        while (true)
        {
            using var list = await client.PostAsJsonAsync($"{_url.TrimEnd('/')}/storage/v1/object/list/{bucket}",
                new { prefix = pin + "/", limit = 1000, offset = 0, sortBy = new { column = "name", order = "asc" } }, ct);
            list.EnsureSuccessStatusCode();
            var files = await list.Content.ReadFromJsonAsync<System.Text.Json.JsonElement[]>(ct) ?? [];
            if (files.Length == 0) return;
            var prefixes = files.Select(x => x.GetProperty("name").GetString()).Select(name =>
            {
                if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\\'))
                    throw new InvalidOperationException("Unexpected object in PIN folder.");
                return $"{pin}/{name}";
            }).ToArray();
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"{_url.TrimEnd('/')}/storage/v1/object/{bucket}")
                { Content = JsonContent.Create(new { prefixes }) };
            using var deleted = await client.SendAsync(delete, ct);
            deleted.EnsureSuccessStatusCode();
        }
    }

    public async Task DeleteObjectAsync(string pin, string measurementId, CancellationToken ct)
    {
        if (!MonitoringApi.Data.CrewMember.IsValid(pin) || measurementId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-'))
            throw new ArgumentException("Invalid photo object path.");
        if (string.IsNullOrWhiteSpace(_url) || string.IsNullOrWhiteSpace(_serviceRoleKey)) throw new InvalidOperationException("Supabase storage is not configured.");
        var bucket = Uri.EscapeDataString(configuration["Supabase:Bucket"] ?? "photos");
        using var client = httpClientFactory.CreateClient("supabase");
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{_url.TrimEnd('/')}/storage/v1/object/{bucket}")
            { Content = JsonContent.Create(new { prefixes = new[] { $"{pin}/{measurementId}.jpg" } }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceRoleKey);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
