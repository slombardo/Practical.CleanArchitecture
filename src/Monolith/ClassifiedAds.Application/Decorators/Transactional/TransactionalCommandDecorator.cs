using ClassifiedAds.Application.Common.Commands;
using ClassifiedAds.Domain.Repositories;
using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Application.Decorators.Transactional;

/// <summary>
/// Decorator that wraps command handlers in a database transaction.
/// Applies to commands marked with [Transactional] attribute or implementing ITransactionalCommand.
/// Ensures atomic writes, deterministic rollback on failure, and emits OpenTelemetry spans for observability.
/// </summary>
[Mapping(Type = typeof(TransactionalAttribute))]
public class TransactionalCommandDecorator<TCommand> : ICommandHandler<TCommand>
    where TCommand : ICommand
{
    private readonly ICommandHandler<TCommand> _handler;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<TransactionalCommandDecorator<TCommand>> _logger;
    private readonly TransactionalAttribute _options;

    public TransactionalCommandDecorator(
        ICommandHandler<TCommand> handler,
        IUnitOfWork unitOfWork,
        ILogger<TransactionalCommandDecorator<TCommand>> logger,
        TransactionalAttribute options)
    {
        _handler = handler;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _options = options;
    }

    public async Task HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        var commandName = typeof(TCommand).Name;
        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        var isolationLevel = _options?.IsolationLevel ?? IsolationLevel.ReadCommitted;

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
                "[TransactionalCommandDecorator] Starting transaction for command {CommandName} with CorrelationId {CorrelationId}",
                commandName,
                correlationId);

            using (await _unitOfWork.BeginTransactionAsync(isolationLevel, cancellationToken))
            {
                try
                {
                    await _handler.HandleAsync(command, cancellationToken);
                    await _unitOfWork.CommitTransactionAsync(cancellationToken);

                    success = true;
                    _logger.LogInformation(
                        "[TransactionalCommandDecorator] Transaction committed successfully for command {CommandName} with CorrelationId {CorrelationId}",
                        commandName,
                        correlationId);
                }
                catch (Exception ex)
                {
                    rolledBack = true;
                    errorCode = DetermineErrorCode(ex);

                    _logger.LogError(
                        ex,
                        "[TransactionalCommandDecorator] Transaction rolled back for command {CommandName} with CorrelationId {CorrelationId}, ErrorCode: {ErrorCode}",
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

    private string DetermineErrorCode(Exception exception)
    {
        // Determine error codes for telemetry purposes
        // The actual HTTP mapping is done in the ProblemDetailsMapper middleware
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
