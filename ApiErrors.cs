using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MonitoringApi.Models;

namespace MonitoringApi;

public static class ApiErrors
{
    public static ApiErrorEnvelope Envelope(HttpContext context, int status, string? code = null) =>
        new(new ApiError(code ?? Code(status), Message(status), context.TraceIdentifier, []));
    public static string Code(int status) => status switch
    {
        400 => "INVALID_REQUEST", 401 => "UNAUTHORIZED", 403 => "FORBIDDEN", 404 => "NOT_FOUND",
        409 => "CONFLICT", 410 => "RESOURCE_RETIRED_OR_EXPIRED", 413 => "PAYLOAD_TOO_LARGE",
        415 => "UNSUPPORTED_MEDIA_TYPE", 422 => "INVALID_VALUE", 429 => "RATE_LIMITED",
        503 => "SERVICE_UNAVAILABLE", _ => "REQUEST_FAILED"
    };
    private static string Message(int status) => status switch
    {
        400 => "The request is invalid.", 401 => "A valid API or device credential is required.",
        403 => "This caller is not permitted to perform this operation.", 404 => "The requested resource was not found.",
        409 => "The request conflicts with existing data.", 410 => "The PIN is retired or the upload has expired.",
        413 => "The request exceeds the size limit.", 415 => "The media type is not supported.",
        422 => "One or more values are outside the accepted range.", 429 => "Too many requests; retry later.",
        503 => "An external service is temporarily unavailable.", _ => "The request could not be completed."
    };
    public static async Task WriteAsync(HttpContext context)
    {
        context.Response.ContentLength = null;
        await context.Response.WriteAsJsonAsync(Envelope(context, context.Response.StatusCode),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }, context.RequestAborted);
    }
}

public sealed class ApiErrorResultFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        var status = context.Result switch { ObjectResult o => o.StatusCode ?? 200, StatusCodeResult s => s.StatusCode, _ => 200 };
        if (status >= 400 && context.Result is not ObjectResult { Value: ApiErrorEnvelope })
            context.Result = new ObjectResult(ApiErrors.Envelope(context.HttpContext, status)) { StatusCode = status };
    }
    public void OnResultExecuted(ResultExecutedContext context) { }
}

public sealed class ApiErrorMiddleware(RequestDelegate next, ILogger<ApiErrorMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "API request {RequestId} failed with {ExceptionType}", context.TraceIdentifier, exception.GetType().Name);
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.Clear();
            context.Response.StatusCode = exception switch
            {
                BadHttpRequestException bad => bad.StatusCode,
                HttpRequestException or TaskCanceledException => 503,
                _ => 500
            };
            await ApiErrors.WriteAsync(context);
        }
    }
}

public sealed class ApiValidationResponseFactory
{
    public static IActionResult Create(ActionContext context) =>
        new BadRequestObjectResult(ApiErrors.Envelope(context.HttpContext, 400));
}
