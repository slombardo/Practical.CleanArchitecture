using ClassifiedAds.Application.Common.Behaviors;
using ClassifiedAds.Domain.Entities;
using ClassifiedAds.Domain.Repositories;
using MediatR;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Application.Orders.Commands;

/// <summary>
/// MediatR request for creating an order with transactional behavior.
/// </summary>
public class CreateOrderRequest : IRequest<CreateOrderResponse>, ITransactionalRequest
{
    public Guid UserId { get; set; }

    public string ExternalOrderRef { get; set; }

    public string Description { get; set; }

    public decimal TotalAmount { get; set; }
}

public class CreateOrderResponse
{
    public Guid OrderId { get; set; }
}

internal class CreateOrderRequestHandler : IRequestHandler<CreateOrderRequest, CreateOrderResponse>
{
    private readonly IRepository<Order, Guid> _orderRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFailureInjector _failureInjector;

    public CreateOrderRequestHandler(
        IRepository<Order, Guid> orderRepository,
        IUnitOfWork unitOfWork,
        IFailureInjector failureInjector)
    {
        _orderRepository = orderRepository;
        _unitOfWork = unitOfWork;
        _failureInjector = failureInjector;
    }

    public async Task<CreateOrderResponse> Handle(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            ExternalOrderRef = request.ExternalOrderRef,
            Description = request.Description,
            TotalAmount = request.TotalAmount,
            Status = "Pending"
        };

        await _orderRepository.AddAsync(order, cancellationToken);

        // Failure injection point for testing
        _failureInjector.CheckForFailure("BeforeSave");

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Failure injection point for testing after save
        _failureInjector.CheckForFailure("AfterSave");

        return new CreateOrderResponse { OrderId = order.Id };
    }
}
