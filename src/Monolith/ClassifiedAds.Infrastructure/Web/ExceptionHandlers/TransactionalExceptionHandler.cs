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
        // Language-independent detection of unique constraint violations using error codes
        var innerException = exception.InnerException;

        if (innerException == null)
        {
            return false;
        }

        // SQL Server: Use error numbers (language-independent)
        if (innerException is SqlException sqlException)
        {
            // 2601 - Cannot insert duplicate key row in object with unique index
            // 2627 - Violation of UNIQUE KEY constraint
            return sqlException.Number == 2601 || sqlException.Number == 2627;
        }

        // PostgreSQL: Check for error code 23505 (unique_violation)
        var postgresType = innerException.GetType().FullName;
        if (postgresType?.Contains("Npgsql.PostgresException") == true)
        {
            // Use reflection to get SqlState property (error code 23505)
            var sqlStateProperty = innerException.GetType().GetProperty("SqlState");
            var sqlState = sqlStateProperty?.GetValue(innerException) as string;
            return sqlState == "23505"; // unique_violation
        }

        // MySQL: Check for error numbers 1062 (duplicate entry) or 1586 (duplicate key)
        var mysqlType = innerException.GetType().FullName;
        if (mysqlType?.Contains("MySql.Data.MySqlClient.MySqlException") == true ||
            mysqlType?.Contains("MySqlConnector.MySqlException") == true)
        {
            var numberProperty = innerException.GetType().GetProperty("Number");
            var errorNumber = numberProperty?.GetValue(innerException);
            return errorNumber is 1062 or 1586;
        }

        // SQLite: Check for SQLITE_CONSTRAINT (19) with UNIQUE constraint type
        var sqliteType = innerException.GetType().FullName;
        if (sqliteType?.Contains("Microsoft.Data.Sqlite.SqliteException") == true)
        {
            var sqliteErrorCodeProperty = innerException.GetType().GetProperty("SqliteErrorCode");
            var errorCode = sqliteErrorCodeProperty?.GetValue(innerException);
            // SQLITE_CONSTRAINT = 19, check message contains "UNIQUE" for constraint type
            if (errorCode is 19)
            {
                var message = innerException.Message ?? string.Empty;
                return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Fallback: Could not determine database provider or error type
        // Log warning and return false to avoid false positives
        _logger.LogWarning(
            "Could not determine if DbUpdateException is a unique constraint violation. " +
            "Exception type: {ExceptionType}, Message: {Message}",
            innerException.GetType().FullName,
            innerException.Message);
        return false;
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
