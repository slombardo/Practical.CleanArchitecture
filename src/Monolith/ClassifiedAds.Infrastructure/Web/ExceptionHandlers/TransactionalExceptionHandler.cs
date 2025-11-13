using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Infrastructure.Web.ExceptionHandlers;

/// <summary>
/// Exception handler that maps transactional exceptions to precise HTTP status codes with ProblemDetails.
/// Maps DbUpdateConcurrencyException and unique constraint violations to 409 Conflict,
/// and all other unhandled exceptions to 500 Internal Server Error with correlation ID.
/// </summary>
public class TransactionalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<TransactionalExceptionHandler> _logger;

    public TransactionalExceptionHandler(ILogger<TransactionalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString();

        // Handle DbUpdateConcurrencyException -> 409 ConcurrencyConflict
        if (exception is DbUpdateConcurrencyException concurrencyException)
        {
            _logger.LogWarning(
                concurrencyException,
                "[TransactionalExceptionHandler] Concurrency conflict detected. CorrelationId: {CorrelationId}",
                correlationId);

            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Concurrency Conflict",
                Detail = "The resource was modified by another user. Please refresh and try again.",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.8",
                Instance = httpContext.Request.Path
            };

            problemDetails.Extensions.Add("code", "ConcurrencyConflict");
            problemDetails.Extensions.Add("correlationId", correlationId);
            problemDetails.Extensions.Add("traceId", Activity.Current?.TraceId.ToString());

            await WriteProblemDetailsAsync(httpContext, problemDetails, cancellationToken);
            return true;
        }

        // Handle DbUpdateException (check for unique constraint violation) -> 409 DuplicateDetected
        if (exception is DbUpdateException dbUpdateException)
        {
            if (IsUniqueConstraintViolation(dbUpdateException))
            {
                _logger.LogWarning(
                    dbUpdateException,
                    "[TransactionalExceptionHandler] Duplicate key detected. CorrelationId: {CorrelationId}",
                    correlationId);

                var problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Duplicate Detected",
                    Detail = "A resource with the same unique key already exists.",
                    Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.8",
                    Instance = httpContext.Request.Path
                };

                problemDetails.Extensions.Add("code", "DuplicateDetected");
                problemDetails.Extensions.Add("correlationId", correlationId);
                problemDetails.Extensions.Add("traceId", Activity.Current?.TraceId.ToString());

                await WriteProblemDetailsAsync(httpContext, problemDetails, cancellationToken);
                return true;
            }

            // Other DbUpdateExceptions -> 500
            _logger.LogError(
                dbUpdateException,
                "[TransactionalExceptionHandler] Database update failed. CorrelationId: {CorrelationId}",
                correlationId);

            var dbProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Internal Server Error",
                Detail = "An unexpected error occurred while updating the database.",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.1",
                Instance = httpContext.Request.Path
            };

            dbProblemDetails.Extensions.Add("code", "DatabaseUpdateFailed");
            dbProblemDetails.Extensions.Add("correlationId", correlationId);
            dbProblemDetails.Extensions.Add("traceId", Activity.Current?.TraceId.ToString());

            await WriteProblemDetailsAsync(httpContext, dbProblemDetails, cancellationToken);
            return true;
        }

        // Not handled by this handler, let other handlers process it
        return false;
    }

    private bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        // Provider-agnostic detection of unique constraint violations
        var innerException = exception.InnerException;

        if (innerException == null)
        {
            return false;
        }

        var message = innerException.Message?.ToLowerInvariant() ?? string.Empty;

        // SQL Server specific checks
        if (innerException is SqlException sqlException)
        {
            // SQL Server error codes for unique constraint violations:
            // 2601 - Cannot insert duplicate key row
            // 2627 - Violation of unique constraint
            return sqlException.Number == 2601 || sqlException.Number == 2627;
        }

        // Generic checks for other providers (PostgreSQL, MySQL, SQLite)
        return message.Contains("unique") ||
               message.Contains("duplicate") ||
               message.Contains("constraint") ||
               message.Contains("violation") ||
               (message.Contains("index") && (message.Contains("duplicate") || message.Contains("unique")));
    }

    private async Task WriteProblemDetailsAsync(
        HttpContext httpContext,
        ProblemDetails problemDetails,
        CancellationToken cancellationToken)
    {
        httpContext.Response.StatusCode = problemDetails.Status ?? StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/problem+json";

        var json = JsonSerializer.Serialize(problemDetails, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        await httpContext.Response.WriteAsync(json, cancellationToken);
    }
}
