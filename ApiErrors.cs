using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MonitoringApi.Models;

namespace MonitoringApi;

public sealed class ApiErrorMiddleware(RequestDelegate next, ILogger<ApiErrorMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        var tracker = new TrackingWriteStream(context.Response.Body);
        context.Response.Body = tracker;
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            logger.LogError("Unhandled API exception for request {RequestId}: {ExceptionType}", context.TraceIdentifier, exception.GetType().Name);
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                tracker.Reset();
            }
        }
        finally
        {
            context.Response.Body = tracker.Inner;
        }
        if (context.Response.StatusCode >= 400 && tracker.BytesWritten == 0 && !context.Response.HasStarted)
        {
            var (code, message) = context.Response.StatusCode switch
            {
                400 => ("INVALID_REQUEST", "The request could not be accepted."),
                401 => ("UNAUTHORIZED", "Authentication is required or invalid."),
                403 => ("FORBIDDEN", "The request is not permitted."),
                404 => ("NOT_FOUND", "The requested resource was not found."),
                409 => ("CONFLICT", "The request conflicts with an existing resource."),
                410 => ("UPLOAD_CLOSED", "The photo upload is closed or expired."),
                413 => ("PAYLOAD_TOO_LARGE", "The uploaded photo exceeds the allowed size."),
                415 => ("UNSUPPORTED_MEDIA_TYPE", "The media type is not supported."),
                422 => ("INVALID_VALUE", "One or more values are outside the accepted range."),
                423 => ("TEMPORARY_LOCKOUT", "Authentication is temporarily locked."),
                429 => ("RATE_LIMITED", "Too many requests. Try again later."),
                _ => ("REQUEST_FAILED", "The request could not be completed.")
            };
            context.Response.ContentLength = null;
            context.Response.ContentType = "application/json";
            var envelope = new ApiErrorEnvelope(new ApiError(code, message, RequestId(context), []));
            await JsonSerializer.SerializeAsync(context.Response.Body, envelope, JsonOptions, context.RequestAborted);
        }
    }

    public static string RequestId(HttpContext context)
    {
        var id = $"req_{Guid.NewGuid():N}";
        context.Items["api_request_id"] = id;
        return id;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed class TrackingWriteStream(Stream inner) : Stream
    {
        public Stream Inner => inner;
        public long BytesWritten { get; private set; }
        public void Reset() => BytesWritten = 0;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); BytesWritten += count; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await inner.WriteAsync(buffer, cancellationToken); BytesWritten += buffer.Length; }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken); BytesWritten += count; }
    }
}

public sealed class ApiValidationResponseFactory
{
    public static IActionResult Create(ActionContext context)
    {
        var error = new ApiError("INVALID_REQUEST", "The request could not be accepted.",
            ApiErrorMiddleware.RequestId(context.HttpContext), []);
        return new BadRequestObjectResult(new ApiErrorEnvelope(error));
    }
}
