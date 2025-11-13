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
            var errorCode = ex.GetType().Name;

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
}
