using System.ComponentModel.DataAnnotations;

namespace MonitoringApi.Models;

public sealed record AstronautDto(string Pin, string Username, string DisplayName);
public sealed record PageDto<T>(IReadOnlyList<T> Items, string? NextCursor);
public sealed record SessionResponse(string AccessToken, string TokenType, int ExpiresIn, string RefreshToken,
    int RefreshExpiresIn, SessionSummary Session);
public sealed record SessionSummary(string SessionId, AstronautDto Astronaut);
public sealed record CurrentSessionResponse(string SessionId, AstronautDto Astronaut, DateTimeOffset ExpiresAt);
public sealed record WaveformDto([Range(1, 200)] int SampleRateHz, [Range(1, 16)] int AdcResolutionBits,
    [MaxLength(4000)] int[] Samples);
public sealed record PulseDto([Range(0, 250)] int Bpm, string Quality, [StringLength(80)] string AlgorithmVersion,
    WaveformDto? Waveform, [StringLength(160)] string? StatusReason = null);
public sealed record PhotoExpectationDto(string CameraId, string MediaType, int Width, int Height);
public sealed record CreateMeasurementRequest(string ClientMeasurementId, DateTimeOffset MeasuredAt,
    PulseDto Pulse, PhotoExpectationDto Photo);
public sealed record CreateMeasurementResponse(string MeasurementId, string ClientMeasurementId, string Pin,
    DateTimeOffset CreatedAt, string PhotoStatus, DateTimeOffset PhotoUploadExpiresAt);
public sealed record MeasurementDto(string MeasurementId, DateTimeOffset MeasuredAt, PulseSummary Pulse,
    string PhotoStatus, string? PhotoUrl);
public sealed record PulseSummary(int Bpm, string Quality, string? StatusReason = null);
public sealed record EnvironmentRequest(string ClientReadingId, DateTimeOffset MeasuredAt, string DeviceId,
    string Sensor, double TemperatureC, double RelativeHumidityPercent);
public sealed record EnvironmentResponse(string ReadingId, string ClientReadingId, string Pin,
    DateTimeOffset ReceivedAt, string Status);
public sealed record PhotoResponse(string MeasurementId, string PhotoStatus, DateTimeOffset ReceivedAt);
public sealed record ApiErrorEnvelope(ApiError Error);
public sealed record ApiError(string Code, string Message, string RequestId, IReadOnlyList<object> Details);

