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
/// MediatR pipeline behavior that wraps command handlers in a database transaction.
/// Applies to commands implementing ITransactionalCommand.
/// Ensures atomic writes, deterministic rollback on failure, and emits OpenTelemetry spans for observability.
/// </summary>
public class TransactionalBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
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
        // Only apply transactional behavior to commands marked with ITransactionalCommand
        if (request is not Commands.ITransactionalCommand)
        {
            // Pass through for non-transactional commands (queries, etc.)
            return await next();
        }

        var commandName = typeof(TRequest).Name;
        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        var isolationLevel = IsolationLevel.ReadCommitted;

        using var activity = Activity.Current?.Source.StartActivity("command.transaction", ActivityKind.Internal);
        activity?.SetTag("command", commandName);
        activity?.SetTag("correlation_id", correlationId);
        activity?.SetTag("isolation_level", isolationLevel.ToString());

        var success = false;
        var rolledBack = false;
        string errorCode = null;

        try
        {
            _logger.LogInformation(
                "[TransactionalBehavior] Starting transaction for command {CommandName} with CorrelationId {CorrelationId}",
                commandName,
                correlationId);

            using (await _unitOfWork.BeginTransactionAsync(isolationLevel, cancellationToken))
            {
                try
                {
                    var response = await next();
                    await _unitOfWork.CommitTransactionAsync(cancellationToken);

                    success = true;
                    _logger.LogInformation(
                        "[TransactionalBehavior] Transaction committed successfully for command {CommandName} with CorrelationId {CorrelationId}",
                        commandName,
                        correlationId);

                    return response;
                }
                catch (Exception ex)
                {
                    rolledBack = true;
                    errorCode = DetermineErrorCode(ex);

                    _logger.LogError(
                        ex,
                        "[TransactionalBehavior] Transaction rolled back for command {CommandName} with CorrelationId {CorrelationId}, ErrorCode: {ErrorCode}",
                        commandName,
                        correlationId,
                        errorCode);

                    // Re-throw to allow exception handler middleware to map to HTTP status
                    throw;
                }
            }
        }
        finally
        {
            // Emit telemetry
            activity?.SetTag("success", success);
            activity?.SetTag("rolled_back", rolledBack);
            if (errorCode != null)
            {
                activity?.SetTag("error_code", errorCode);
            }
        }
    }

    private static string DetermineErrorCode(Exception exception)
    {
        // Determine error codes for telemetry purposes
        // The actual HTTP mapping is done in the TransactionalExceptionHandler middleware
        var exceptionType = exception.GetType().Name;

        if (exceptionType.Contains("DbUpdateConcurrencyException"))
        {
            return "ConcurrencyConflict";
        }

        if (exceptionType.Contains("DbUpdateException"))
        {
            // Check if it's a unique constraint violation
            var innerException = exception.InnerException;
            if (innerException != null)
            {
                var message = innerException.Message?.ToLowerInvariant() ?? string.Empty;
                if (message.Contains("unique") ||
                    message.Contains("duplicate") ||
                    message.Contains("constraint") ||
                    message.Contains("violation") ||
                    message.Contains("index"))
                {
                    return "DuplicateDetected";
                }
            }
        }

        return "UnexpectedError";
    }
}
