using FinSight.Core.Abstractions;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Insights;
using FinSight.Infrastructure.Pipeline;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FinSight.Api.Middleware;

/// <summary>
/// The error contract: <c>{ status, code, message }</c>. <c>code</c> is stable for the client;
/// <c>message</c> is safe, human-readable copy. Stack traces and exception text never leave the server.
/// </summary>
public static class ApiErrors
{
    public static ObjectResult Problem(int status, string code, string message) =>
        new(Create(status, code, message)) { StatusCode = status };

    public static ProblemDetails Create(int status, string code, string message)
    {
        var problem = new ProblemDetails { Status = status, Title = message };
        problem.Extensions["code"] = code;
        problem.Extensions["message"] = message;
        return problem;
    }

    public static ObjectResult NotFound(string what = "item") =>
        Problem(StatusCodes.Status404NotFound, "not_found", $"That {what} doesn't exist or was deleted.");

    public static ObjectResult BadRequest(string code, string message) =>
        Problem(StatusCodes.Status400BadRequest, code, message);

    public static ObjectResult Map(Exception exception) => exception switch
    {
        GmailAuthExpiredException => Problem(StatusCodes.Status409Conflict, StatementFailure.GmailAuthExpired, StatementFailure.Message(StatementFailure.GmailAuthExpired)),
        GmailNotConnectedException => Problem(StatusCodes.Status409Conflict, StatementFailure.GmailNotConnected, StatementFailure.Message(StatementFailure.GmailNotConnected)),
        AnalysisUnavailableException { Reason: AnalysisUnavailableReason.Disabled } => Problem(StatusCodes.Status409Conflict, "ai_disabled", "AI insights are turned off in Settings."),
        AnalysisUnavailableException { Reason: AnalysisUnavailableReason.NotConfigured } or AiUnavailableException { Failure: AiFailure.NotConfigured } =>
            Problem(StatusCodes.Status503ServiceUnavailable, "ai_not_configured", "AI analysis isn't set up on this server. Your transaction data is still available."),
        AnalysisUnavailableException { Reason: AnalysisUnavailableReason.NotEnoughData } => Problem(StatusCodes.Status422UnprocessableEntity, "not_enough_data", "There are no transactions in this period to analyze."),
        AiUnavailableException { Failure: AiFailure.RateLimited } => Problem(StatusCodes.Status429TooManyRequests, "ai_rate_limited", "AI analysis is busy right now. Try again in a minute."),
        AiUnavailableException => Problem(StatusCodes.Status503ServiceUnavailable, "ai_unavailable", "AI analysis is temporarily unavailable. Your transaction data is still available."),
        ArgumentException => Problem(StatusCodes.Status400BadRequest, "invalid_request", "That request wasn't valid."),
        UnauthorizedAccessException => Problem(StatusCodes.Status403Forbidden, "forbidden", "You don't have access to that."),
        _ => Problem(StatusCodes.Status500InternalServerError, "unexpected", "Something went wrong on our side. Try again."),
    };
}

public sealed partial class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return true;
        }

        var result = ApiErrors.Map(exception);
        if (result.StatusCode >= 500)
        {
            // Type and path only: messages and inner data can contain financial details.
            LogUnhandled(logger, exception.GetType().Name, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = result.StatusCode ?? 500;
        await httpContext.Response.WriteAsJsonAsync(result.Value, cancellationToken);
        return true;
    }

    [LoggerMessage(LogLevel.Error, "Unhandled {ExceptionType} on {Path}")]
    private static partial void LogUnhandled(ILogger logger, string exceptionType, string path);
}
