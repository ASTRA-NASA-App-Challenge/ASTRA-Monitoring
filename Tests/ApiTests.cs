using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitoringApi.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MonitoringApi.Tests;

public class ApiTests
{
    private const string Pin = "012345";
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    { using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return doc.RootElement.Clone(); }
    private static object Measurement(string? clientId = null, int bpm = 72) => new
    {
        client_measurement_id = clientId ?? Guid.NewGuid().ToString(), measured_at = "2026-11-15T10:00:00Z",
        pulse = new { bpm, quality = "good", algorithm_version = "test-v1" },
        photo = new { camera_id = "cam-test", media_type = "image/jpeg", width = 320, height = 240 }
    };
    private static object Reading(string? clientId = null, int temperature = 22) => new
    { client_reading_id = clientId ?? Guid.NewGuid().ToString(), measured_at = "2026-11-15T10:00:00Z", device_id = "uno-test", sensor = "DHT11", temperature_c = temperature, relative_humidity_percent = 45 };
    private static byte[] Jpeg()
    { using var image = new Image<Rgb24>(320, 240); using var stream = new MemoryStream(); image.SaveAsJpeg(stream); return stream.ToArray(); }
    private static async Task<string> Create(HttpClient admin)
    {
        await admin.PostAsJsonAsync("/api/v1/crew", new { pin = Pin });
        var response = await admin.PostAsJsonAsync($"/api/v1/crew/{Pin}/measurements", Measurement());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("measurement_id").GetString()!;
    }
    private static async Task<HttpResponseMessage> Upload(HttpClient client, string path)
    { var content = new ByteArrayContent(Jpeg()); content.Headers.ContentType = new("image/jpeg"); return await client.PutAsync(path, content); }

    [Fact]
    public async Task AuthenticationAndErrorsAreConsistent()
    {
        using var api = new ApiFactory(); using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/v1/healthz")).StatusCode);
        var response = await anonymous.PostAsJsonAsync("/api/v1/crew", new { pin = Pin });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = (await Json(response)).GetProperty("error"); Assert.Equal("UNAUTHORIZED", error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("request_id").GetString()));
        using var uno = api.Uno();
        Assert.Equal(HttpStatusCode.Forbidden, (await uno.DeleteAsync($"/api/v1/crew/{Pin}")).StatusCode);
        using var admin = api.Admin();
        var invalid = await admin.PostAsJsonAsync("/api/v1/crew", new { pin = "bad" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("INVALID_REQUEST", (await Json(invalid)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RegistrationIsIdempotentAndPreservesLeadingZeroes()
    {
        using var api = new ApiFactory(); using var admin = api.Admin();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/crew", new { pin = Pin })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/crew", new { pin = Pin })).StatusCode);
        Assert.Equal(1, await api.Db(db => db.CrewMembers.CountAsync()));
        Assert.Equal(Pin, await api.Db(db => db.CrewMembers.Select(x => x.Pin).SingleAsync()));
    }

    [Fact]
    public async Task MeasurementRetriesReturnOneRowAndConflictsRejectChanges()
    {
        using var api = new ApiFactory(); using var admin = api.Admin();
        await admin.PostAsJsonAsync("/api/v1/crew", new { pin = Pin });
        var id = Guid.NewGuid().ToString(); var path = $"/api/v1/crew/{Pin}/measurements";
        var first = await admin.PostAsJsonAsync(path, Measurement(id));
        var retry = await admin.PostAsJsonAsync(path, Measurement(id.ToUpperInvariant()));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode); Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal((await Json(first)).GetProperty("measurement_id").GetString(), (await Json(retry)).GetProperty("measurement_id").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(path, Measurement(id, 80))).StatusCode);
        Assert.Equal(1, await api.Db(db => db.Measurements.CountAsync()));
    }

    [Fact]
    public async Task EnvironmentRetriesAndPaginationAreStable()
    {
        using var api = new ApiFactory(); using var admin = api.Admin();
        await admin.PostAsJsonAsync("/api/v1/crew", new { pin = Pin });
        var path = $"/api/v1/crew/{Pin}/environment-readings"; var id = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(path, Reading(id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(path, Reading(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(path, Reading(id, 23))).StatusCode);
        await admin.PostAsJsonAsync(path, Reading());
        var query = "?from=2026-11-15T00:00:00Z&to=2026-11-16T00:00:00Z&limit=1";
        var first = await Json(await admin.GetAsync(path + query));
        Assert.Single(first.GetProperty("items").EnumerateArray());
        var cursor = first.GetProperty("next_cursor").GetString(); Assert.NotNull(cursor);
        var second = await Json(await admin.GetAsync(path + query + "&cursor=" + Uri.EscapeDataString(cursor)));
        Assert.Single(second.GetProperty("items").EnumerateArray()); Assert.Equal(JsonValueKind.Null, second.GetProperty("next_cursor").ValueKind);
        Assert.NotEqual(first.GetProperty("items")[0].GetProperty("reading_id").GetString(), second.GetProperty("items")[0].GetProperty("reading_id").GetString());
        var bad = Convert.ToBase64String(Encoding.UTF8.GetBytes(long.MaxValue + ":env_bad"));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(path + query + "&cursor=" + Uri.EscapeDataString(bad))).StatusCode);
    }

    [Fact]
    public async Task UploadFailureIsRetryableAndPhotosUsePrivatePinFolder()
    {
        using var api = new ApiFactory(); using var admin = api.Admin(); var id = await Create(admin);
        var path = $"/api/v1/crew/{Pin}/measurements/{id}/photo";
        api.Storage.FailUploads = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Upload(admin, path)).StatusCode);
        Assert.Equal("awaiting_upload", await api.Db(db => db.Measurements.Select(x => x.PhotoStatus).SingleAsync()));
        api.Storage.FailUploads = false;
        Assert.Equal(HttpStatusCode.OK, (await Upload(admin, path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Upload(admin, path)).StatusCode);
        Assert.True(api.Storage.Objects.ContainsKey($"{Pin}/{id}.jpg"));
        var photo = await admin.GetAsync(path); Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal("image/jpeg", photo.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", photo.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/crew/999999/measurements/{id}/photo")).StatusCode);
    }

    [Fact]
    public async Task DeletionRemovesDataAndPhotosAndNeverReusesPin()
    {
        using var api = new ApiFactory(); using var admin = api.Admin(); var id = await Create(admin);
        await admin.PostAsJsonAsync($"/api/v1/crew/{Pin}/environment-readings", Reading());
        await Upload(admin, $"/api/v1/crew/{Pin}/measurements/{id}/photo");
        api.Storage.Objects["999999/other.jpg"] = [1];
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/crew/{Pin}")).StatusCode);
        Assert.Equal(0, await api.Db(db => db.Measurements.CountAsync()));
        Assert.Equal(0, await api.Db(db => db.EnvironmentReadings.CountAsync()));
        Assert.True(await api.Db(db => db.CrewMembers.Where(x => x.Pin == Pin).Select(x => x.Retired).SingleAsync()));
        Assert.Single(api.Storage.Objects); Assert.True(api.Storage.Objects.ContainsKey("999999/other.jpg"));
        Assert.Equal(HttpStatusCode.Gone, (await admin.PostAsJsonAsync("/api/v1/crew", new { pin = Pin })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/crew/{Pin}")).StatusCode);
    }

    [Fact]
    public async Task StorageOutageLeavesDurableCleanupAndRetiresBeforeDelayedRegistration()
    {
        using var api = new ApiFactory(); using var admin = api.Admin();
        api.Storage.Objects[$"{Pin}/orphan.jpg"] = [1]; api.Storage.FailDeletes = true;
        Assert.Equal(HttpStatusCode.Accepted, (await admin.DeleteAsync($"/api/v1/crew/{Pin}")).StatusCode);
        Assert.True(await api.Db(db => db.CrewMembers.Select(x => x.DeletionPending).SingleAsync()));
        Assert.Equal(HttpStatusCode.Gone, (await admin.PostAsJsonAsync("/api/v1/crew", new { pin = Pin })).StatusCode);
        api.Storage.FailDeletes = false;
        await using(var scope = api.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<CrewDeletion>().PurgePhotosAsync(Pin, default);
        Assert.Empty(api.Storage.Objects);
        Assert.False(await api.Db(db => db.CrewMembers.Select(x => x.DeletionPending).SingleAsync()));
    }

    [Fact]
    public async Task LostUploadResponseLeavesRecoverableCleanupWithoutDroppingTheReading()
    {
        using var api = new ApiFactory(); using var admin = api.Admin(); var id = await Create(admin);
        api.Storage.FailAfterStore = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Upload(admin, $"/api/v1/crew/{Pin}/measurements/{id}/photo")).StatusCode);
        Assert.True(api.Storage.Objects.ContainsKey($"{Pin}/{id}.jpg"));
        Assert.Equal(1, await api.Db(db => db.PhotoUploadLeases.CountAsync()));
        await using (var scope = api.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<PhotoOrphanCleanup>().ProcessAsync(default);
        Assert.Empty(api.Storage.Objects);
        Assert.Equal(1, await api.Db(db => db.Measurements.CountAsync()));
        Assert.Equal(0, await api.Db(db => db.PhotoUploadLeases.CountAsync()));
        api.Storage.FailAfterStore = false;
        Assert.Equal(HttpStatusCode.OK, (await Upload(admin, $"/api/v1/crew/{Pin}/measurements/{id}/photo")).StatusCode);
    }

    [Fact]
    public async Task SlowPhotoUploadDoesNotBlockReadingsAndLateObjectsAreDeleted()
    {
        using var api = new ApiFactory(); using var admin = api.Admin(); var id = await Create(admin);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Storage.UploadGate = gate;
        var upload = Upload(admin, $"/api/v1/crew/{Pin}/measurements/{id}/photo");
        try
        {
            await api.Storage.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var reading = await admin.PostAsJsonAsync($"/api/v1/crew/{Pin}/environment-readings", Reading()).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(HttpStatusCode.Created, reading.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await admin.DeleteAsync($"/api/v1/crew/{Pin}")).StatusCode);
        }
        finally { gate.TrySetResult(true); }
        Assert.Equal(HttpStatusCode.Gone, (await upload).StatusCode);
        Assert.True(api.Storage.Objects.ContainsKey($"{Pin}/{id}.jpg"));
        await using (var scope = api.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<CrewDeletion>().PurgePhotosAsync(Pin, default);
        Assert.Empty(api.Storage.Objects);
        Assert.Equal(0, await api.Db(db => db.PhotoUploadLeases.CountAsync()));
    }

    [Fact]
    public async Task ConcurrentRetriesDoNotDuplicateMeasurements()
    {
        using var api = new ApiFactory(); using var admin = api.Admin();
        await admin.PostAsJsonAsync("/api/v1/crew", new { pin = Pin });
        var body = Measurement(Guid.NewGuid().ToString());
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => admin.PostAsJsonAsync($"/api/v1/crew/{Pin}/measurements", body)));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Equal(4, results.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, await api.Db(db => db.Measurements.CountAsync()));
    }

    [Fact]
    public async Task DateUnderflowAndInvalidPhotoBodyReturnStructuredErrors()
    {
        using var api = new ApiFactory(); using var admin = api.Admin(); var id = await Create(admin);
        foreach (var path in new[] { "measurements", "environment-readings" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.GetAsync($"/api/v1/crew/{Pin}/{path}?to=0001-01-01T00:00:00Z")).StatusCode);
        var content = new ByteArrayContent([1, 2, 3]); content.Headers.ContentType = new("image/jpeg");
        var invalid = await admin.PutAsync($"/api/v1/crew/{Pin}/measurements/{id}/photo", content);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("INVALID_VALUE", (await Json(invalid)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExistingFirmwareFlowWorksWithoutNewHeadersOrTimestamps()
    {
        using var api = new ApiFactory(); using var admin = api.Admin(); await admin.PostAsJsonAsync("/api/v1/crew", new { pin = Pin });
        using var uno = api.Uno();
        var roster = await Json(await uno.GetAsync("/api/v1/astronauts"));
        var handle = roster.GetProperty("items")[0].GetProperty("astronaut_id").GetString();
        Assert.NotEqual(Pin, handle);
        var login = await uno.PostAsJsonAsync("/api/v1/auth/sessions", new { astronaut_id = handle, pin = Pin });
        Assert.Equal(HttpStatusCode.Created, login.StatusCode);
        var tokens = await Json(login); uno.DefaultRequestHeaders.Authorization = new("Bearer", tokens.GetProperty("access_token").GetString());
        await admin.PostAsJsonAsync("/api/v1/crew", new { pin = "999999" });
        Assert.Equal(HttpStatusCode.Forbidden, (await uno.PostAsJsonAsync("/api/v1/crew/999999/measurements", Measurement())).StatusCode);
        var id = Guid.NewGuid().ToString();
        var body = new { client_measurement_id = id, pulse = new { bpm = 72, quality = "good", algorithm_version = "demo-1.0" }, photo = new { camera_id = "cam-test", media_type = "image/jpeg", width = 320, height = 240 } };
        var created = await uno.PostAsJsonAsync("/api/v1/measurements", body); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await uno.PostAsJsonAsync("/api/v1/measurements", body)).StatusCode);
        var measurementId = (await Json(created)).GetProperty("measurement_id").GetString();
        using var camera = api.CreateClient(); camera.DefaultRequestHeaders.Add("X-Device-Key", ApiFactory.CameraKey);
        Assert.Equal(HttpStatusCode.OK, (await Upload(camera, $"/api/v1/measurements/{measurementId}/photo")).StatusCode);
        using var tokenOnly = api.CreateClient(); tokenOnly.DefaultRequestHeaders.Authorization = uno.DefaultRequestHeaders.Authorization;
        var status = await Json(await tokenOnly.GetAsync($"/api/v1/measurements/{measurementId}")); Assert.Equal("uploaded", status.GetProperty("photo_status").GetString());
        var reading = new { client_reading_id = Guid.NewGuid().ToString(), client_uptime_ms = 1234, device_id = "uno-test", sensor = "DHT11", temperature_c = 22, relative_humidity_percent = 45 };
        Assert.Equal(HttpStatusCode.Created, (await uno.PostAsJsonAsync("/api/v1/environment-readings", reading)).StatusCode);
        var refresh = await api.CreateClient().PostAsJsonAsync("/api/v1/auth/sessions/refresh", new { refresh_token = tokens.GetProperty("refresh_token").GetString() });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tokenOnly.GetAsync($"/api/v1/measurements/{measurementId}")).StatusCode);
        var updated = await Json(refresh); tokenOnly.DefaultRequestHeaders.Authorization = new("Bearer", updated.GetProperty("access_token").GetString());
        await admin.DeleteAsync($"/api/v1/crew/{Pin}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await tokenOnly.GetAsync($"/api/v1/measurements/{measurementId}")).StatusCode);
        Assert.Equal(0, await api.Db(db => db.DeviceSessions.CountAsync()));
    }
}
