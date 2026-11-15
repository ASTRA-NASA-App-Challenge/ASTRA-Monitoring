using System.Net;
using System.Security.Claims;
using Astra.Demo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MonitoringApi.Controllers;
using MonitoringApi.Data;
using MonitoringApi.Models;
using MonitoringApi.Services;

namespace MonitoringApi.Tests;

public class DemoBatchTests
{
    [Fact]
    public async Task BatchPreservesValidationIdempotencyAndRealPhotoState()
    {
        await using var factory = new ApiFactory();
        using var client = factory.Admin();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        db.CrewMembers.Add(new CrewMember { Pin = "520301" });
        await db.SaveChangesAsync();
        var environment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        environment.EnvironmentName = "Development";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DemoSeedHost"] = "true" }).Build();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("caller", "web")], "test")) };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        var controller = new DemoHistoryController(db, environment, configuration, scope.ServiceProvider.GetRequiredService<SupabasePhotoStorage>())
            { ControllerContext = new ControllerContext { HttpContext = context } };
        var samples = DemoSeries.Samples(DateTimeOffset.UtcNow).Where(x => x.Pin == "520301" && x.Slot is 1 or 2).ToArray();
        DemoHistoryItem Item(DemoSample x) => new(
            new CreateMeasurementRequest(x.MeasurementClientId, x.At,
                new PulseDto(x.Bpm, x.Quality, DemoSeries.Algorithm, null, x.Reason),
                new PhotoExpectationDto("cam-test", "image/jpeg", 320, 240)),
            new EnvironmentRequest(x.EnvironmentClientId, x.At, "uno-test", "DHT11", x.Temperature, x.Humidity));
        var request = new DemoHistoryRequest(samples.Select(Item).ToList());
        var first = Assert.IsType<OkObjectResult>(await controller.Create("520301", request));
        Assert.Equal(2, Assert.IsType<DemoHistoryResponse>(first.Value).MeasurementsCreated);
        var second = Assert.IsType<OkObjectResult>(await controller.Create("520301", request));
        Assert.Equal(0, Assert.IsType<DemoHistoryResponse>(second.Value).MeasurementsCreated);
        Assert.Equal(2, await db.Measurements.CountAsync());
        Assert.Equal(2, await db.EnvironmentReadings.CountAsync());
        Assert.All(await db.Measurements.ToListAsync(), x => { Assert.Equal("awaiting_upload", x.PhotoStatus); Assert.Null(x.PhotoSha256); });
        var invalidSample = DemoSeries.Samples(DateTimeOffset.UtcNow).Single(x => x.Pin == "520301" && x.Slot == 3);
        var invalid = Item(invalidSample);
        invalid = invalid with { Measurement = invalid.Measurement with { Pulse = invalid.Measurement.Pulse with { Bpm = 251 } } };
        Assert.IsType<UnprocessableEntityResult>(await controller.Create("520301", new DemoHistoryRequest([invalid])));
        Assert.Equal(2, await db.Measurements.CountAsync());
        var photoSample = Item(DemoSeries.Samples(DateTimeOffset.UtcNow).Single(x => x.Pin == "520301" && x.Slot == 0));
        Assert.IsType<UnprocessableEntityResult>(await controller.Create("520301", new DemoHistoryRequest([photoSample])));
        await db.Devices.Where(x => x.DeviceId == "cam-test").ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false));
        Assert.IsType<BadRequestResult>(await controller.Create("520301", new DemoHistoryRequest([Item(invalidSample)])));
        Assert.Equal(2, await db.Measurements.CountAsync());
    }

    [Fact]
    public async Task BatchRequiresWebAuthenticationAndIsHiddenOnNormalHosts()
    {
        await using var factory = new ApiFactory();
        using var anonymous = factory.CreateClient();
        using var response = await anonymous.PostAsync("/api/v1/crew/520301/demo-history", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var admin = factory.Admin();
        using var authenticated = await admin.PostAsync("/api/v1/crew/520301/demo-history", new StringContent("{\"items\":[]}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, authenticated.StatusCode);
        using var hiddenStatus = await admin.GetAsync("/api/v1/demo/status");
        Assert.Equal(HttpStatusCode.NotFound, hiddenStatus.StatusCode);
    }

    [Fact]
    public async Task StatusDistinguishesPartialHistoryFromCompletion()
    {
        await using var factory = new ApiFactory();
        using var admin = factory.Admin();
        factory.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName = "Development";
        using var response = await admin.GetAsync("/api/v1/demo/status");
        response.EnsureSuccessStatusCode();
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("waiting_for_pins", json.RootElement.GetProperty("state").GetString());
        Assert.Equal(750, json.RootElement.GetProperty("expected_measurements").GetInt32());
        Assert.Equal(18, json.RootElement.GetProperty("expected_photos").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("measurements").GetInt32());
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync("/api/v1/demo/status");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }
}
