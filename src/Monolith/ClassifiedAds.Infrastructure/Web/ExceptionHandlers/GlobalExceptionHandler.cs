using ClassifiedAds.CrossCuttingConcerns.Exceptions;
using ClassifiedAds.CrossCuttingConcerns.Logging;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Infrastructure.Web.ExceptionHandlers;

public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly GlobalExceptionHandlerOptions _options;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IOptions<GlobalExceptionHandlerOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var response = httpContext.Response;
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString();

        if (exception is NotFoundException)
        {
            var problemDetails = new ProblemDetails
            {
                Detail = exception.Message,
                Instance = null,
                Status = (int)HttpStatusCode.NotFound,
                Title = "Not Found",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.4"
            };

            problemDetails.Extensions.Add("message", exception.Message);
            problemDetails.Extensions.Add("traceId", Activity.Current.GetTraceId());

            response.ContentType = "application/problem+json";
            response.StatusCode = problemDetails.Status.Value;

            var result = JsonSerializer.Serialize(problemDetails);
            await response.WriteAsync(result, cancellationToken: cancellationToken);

            return true;
        }
        else if (exception is ValidationException)
        {
            var problemDetails = new ProblemDetails
            {
                Detail = exception.Message,
                Instance = null,
                Status = (int)HttpStatusCode.BadRequest,
                Title = "Bad Request",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.1"
            };

            problemDetails.Extensions.Add("message", exception.Message);
            problemDetails.Extensions.Add("traceId", Activity.Current.GetTraceId());

            response.ContentType = "application/problem+json";
            response.StatusCode = problemDetails.Status.Value;

            var result = JsonSerializer.Serialize(problemDetails);
            await response.WriteAsync(result, cancellationToken: cancellationToken);

            return true;
        }
        else if (exception is DbUpdateConcurrencyException)
        {
            _logger.LogWarning(
                exception,
                "Concurrency conflict detected. CorrelationId: {CorrelationId}",
                correlationId);

            var problemDetails = new ProblemDetails
            {
                Detail = "A concurrency conflict occurred. The resource was modified by another process.",
                Instance = null,
                Status = (int)HttpStatusCode.Conflict,
                Title = "Conflict",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.8"
            };

            problemDetails.Extensions.Add("code", "ConcurrencyConflict");
            problemDetails.Extensions.Add("correlationId", correlationId);
            problemDetails.Extensions.Add("traceId", Activity.Current.GetTraceId());

            response.ContentType = "application/problem+json";
            response.StatusCode = problemDetails.Status.Value;

            var result = JsonSerializer.Serialize(problemDetails);
            await response.WriteAsync(result, cancellationToken: cancellationToken);

            return true;
        }
        else if (IsUniqueConstraintViolation(exception))
        {
            _logger.LogWarning(
                exception,
                "Duplicate entry detected. CorrelationId: {CorrelationId}",
                correlationId);

            var problemDetails = new ProblemDetails
            {
                Detail = "A duplicate entry was detected. The resource already exists.",
                Instance = null,
                Status = (int)HttpStatusCode.Conflict,
                Title = "Conflict",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.8"
            };

            problemDetails.Extensions.Add("code", "DuplicateDetected");
            problemDetails.Extensions.Add("correlationId", correlationId);
            problemDetails.Extensions.Add("traceId", Activity.Current.GetTraceId());

            response.ContentType = "application/problem+json";
            response.StatusCode = problemDetails.Status.Value;

            var result = JsonSerializer.Serialize(problemDetails);
            await response.WriteAsync(result, cancellationToken: cancellationToken);

            return true;
        }
        else
        {
            _logger.LogError(
                exception,
                "[{Ticks}-{ThreadId}] CorrelationId: {CorrelationId}",
                DateTime.UtcNow.Ticks,
                Environment.CurrentManagedThreadId,
                correlationId);

            if (_options.DetailLevel == GlobalExceptionDetailLevel.Throw)
            {
                return false;
            }

            var problemDetails = new ProblemDetails
            {
                Detail = _options.GetErrorMessage(exception),
                Instance = null,
                Status = (int)HttpStatusCode.InternalServerError,
                Title = "Internal Server Error",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.1"
            };

            problemDetails.Extensions.Add("message", _options.GetErrorMessage(exception));
            problemDetails.Extensions.Add("correlationId", correlationId);
            problemDetails.Extensions.Add("traceId", Activity.Current.GetTraceId());

            response.ContentType = "application/problem+json";
            response.StatusCode = problemDetails.Status.Value;

            var result = JsonSerializer.Serialize(problemDetails);
            await response.WriteAsync(result, cancellationToken: cancellationToken);

            return true;
        }
    }

    private static bool IsUniqueConstraintViolation(Exception exception)
    {
        if (exception is not DbUpdateException dbUpdateException)
        {
            return false;
        }

        var innerException = dbUpdateException.InnerException;
        if (innerException == null)
        {
            return false;
        }

        var message = innerException.Message;
        var typeName = innerException.GetType().FullName;

        // SQL Server: SqlException with error numbers 2601 (unique index) or 2627 (unique constraint)
        if (typeName?.Contains("SqlException") == true)
        {
            return message.Contains("Cannot insert duplicate key") ||
                   message.Contains("Violation of UNIQUE KEY constraint") ||
                   message.Contains("duplicate key");
        }

        // PostgreSQL: PostgresException with SqlState 23505
        if (typeName?.Contains("PostgresException") == true)
        {
            return message.Contains("duplicate key value violates unique constraint") ||
                   message.Contains("23505");
        }

        // MySQL: MySqlException with error number 1062
        if (typeName?.Contains("MySqlException") == true)
        {
            return message.Contains("Duplicate entry") ||
                   message.Contains("for key");
        }

        // Generic fallback: check for common unique constraint violation patterns
        return message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase);
    }
}