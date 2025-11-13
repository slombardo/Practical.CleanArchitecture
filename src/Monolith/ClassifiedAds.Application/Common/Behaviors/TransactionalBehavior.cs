using ClassifiedAds.Domain.Repositories;
using MediatR;
using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Application.Common.Behaviors;

/// <summary>
/// MediatR pipeline behavior that wraps transactional commands in an EF Core transaction.
/// Applies only to requests that implement ITransactionalRequest.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public class TransactionalBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly ActivitySource ActivitySource = new("ClassifiedAds.Application.Transactional");

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<TransactionalBehavior<TRequest, TResponse>> _logger;

    public TransactionalBehavior(
        IUnitOfWork unitOfWork,
        ILogger<TransactionalBehavior<TRequest, TResponse>> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Only apply transactional behavior to requests that implement ITransactionalRequest
        if (request is not ITransactionalRequest)
        {
            return await next();
        }

        var requestName = request.GetType().Name;
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString();

        using var activity = ActivitySource.StartActivity("command.transaction");
        activity?.SetTag("command", requestName);

        _logger.LogInformation(
            "Starting transaction for request {RequestName} with CorrelationId {CorrelationId}",
            requestName,
            correlationId);

        try
        {
            using (await _unitOfWork.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                var response = await next();

                await _unitOfWork.CommitTransactionAsync(cancellationToken);

                activity?.SetTag("success", true);
                activity?.SetTag("rolled_back", false);

                _logger.LogInformation(
                    "Transaction committed successfully for request {RequestName} with CorrelationId {CorrelationId}",
                    requestName,
                    correlationId);

                return response;
            }
        }
        catch (Exception ex)
        {
            var errorCode = MapExceptionToErrorCode(ex);

            activity?.SetTag("success", false);
            activity?.SetTag("rolled_back", true);
            activity?.SetTag("error_code", errorCode);

            _logger.LogError(
                ex,
                "Transaction rolled back for request {RequestName} with CorrelationId {CorrelationId}. Error: {ErrorCode}",
                requestName,
                correlationId,
                errorCode);

            throw;
        }
    }

    private static string MapExceptionToErrorCode(Exception exception)
    {
        var exceptionTypeName = exception.GetType().FullName;

        // DbUpdateConcurrencyException -> ConcurrencyConflict
        if (exceptionTypeName?.Contains("DbUpdateConcurrencyException") == true)
        {
            return "ConcurrencyConflict";
        }

        // DbUpdateException with unique constraint violation -> DuplicateDetected
        if (exceptionTypeName?.Contains("DbUpdateException") == true && IsUniqueConstraintViolation(exception))
        {
            return "DuplicateDetected";
        }

        // Default to exception type name for unknown exceptions
        return exception.GetType().Name;
    }

    private static bool IsUniqueConstraintViolation(Exception exception)
    {
        // Check if it's a DbUpdateException (without direct type reference to avoid EF Core dependency)
        var exceptionTypeName = exception.GetType().FullName;
        if (exceptionTypeName?.Contains("DbUpdateException") != true)
        {
            return false;
        }

        var innerException = exception.InnerException;
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
