using ClassifiedAds.Domain.Repositories;
using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Application.Decorators.Transactional;

[Mapping(Type = typeof(TransactionalAttribute))]
public class TransactionalCommandDecorator<TCommand> : ICommandHandler<TCommand>
    where TCommand : ICommand
{
    private static readonly ActivitySource ActivitySource = new("ClassifiedAds.Application.Decorator.Transactional");

    private readonly ICommandHandler<TCommand> _handler;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<TransactionalCommandDecorator<TCommand>> _logger;

    public TransactionalCommandDecorator(
        ICommandHandler<TCommand> handler,
        IUnitOfWork unitOfWork,
        ILogger<TransactionalCommandDecorator<TCommand>> logger)
    {
        _handler = handler;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        var commandName = command.GetType().Name;
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString();

        using var activity = ActivitySource.StartActivity("command.transaction");
        activity?.SetTag("command", commandName);

        _logger.LogInformation(
            "Starting transaction for command {CommandName} with CorrelationId {CorrelationId}",
            commandName,
            correlationId);

        bool success = false;
        bool rolledBack = false;
        string errorCode = null;

        try
        {
            using (await _unitOfWork.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                await _handler.HandleAsync(command, cancellationToken);

                await _unitOfWork.CommitTransactionAsync(cancellationToken);
                success = true;
            }

            activity?.SetTag("success", true);
            activity?.SetTag("rolled_back", false);

            _logger.LogInformation(
                "Transaction committed successfully for command {CommandName} with CorrelationId {CorrelationId}",
                commandName,
                correlationId);
        }
        catch (Exception ex)
        {
            rolledBack = true;
            errorCode = ex.GetType().Name;

            activity?.SetTag("success", false);
            activity?.SetTag("rolled_back", true);
            activity?.SetTag("error_code", errorCode);

            _logger.LogError(
                ex,
                "Transaction rolled back for command {CommandName} with CorrelationId {CorrelationId}. Error: {ErrorCode}",
                commandName,
                correlationId,
                errorCode);

            throw;
        }
    }
}
