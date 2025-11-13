using ClassifiedAds.Application.Decorators.Transactional;
using ClassifiedAds.Domain.Entities;
using ClassifiedAds.Domain.Repositories;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Application.Orders.Commands;

public class CreateOrderCommand : ITransactionalCommand
{
    public Guid UserId { get; set; }

    public string ExternalOrderRef { get; set; }

    public string Description { get; set; }

    public decimal TotalAmount { get; set; }

    /// <summary>
    /// The created order ID will be set after successful execution.
    /// </summary>
    public Guid CreatedOrderId { get; set; }
}

[Transactional]
internal class CreateOrderCommandHandler : ICommandHandler<CreateOrderCommand>
{
    private readonly IRepository<Order, Guid> _orderRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFailureInjector _failureInjector;

    public CreateOrderCommandHandler(
        IRepository<Order, Guid> orderRepository,
        IUnitOfWork unitOfWork,
        IFailureInjector failureInjector)
    {
        _orderRepository = orderRepository;
        _unitOfWork = unitOfWork;
        _failureInjector = failureInjector;
    }

    public async Task HandleAsync(CreateOrderCommand command, CancellationToken cancellationToken = default)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = command.UserId,
            ExternalOrderRef = command.ExternalOrderRef,
            Description = command.Description,
            TotalAmount = command.TotalAmount,
            Status = "Pending"
        };

        await _orderRepository.AddAsync(order, cancellationToken);

        // Failure injection point for testing
        _failureInjector.CheckForFailure("BeforeSave");

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Failure injection point for testing after save
        _failureInjector.CheckForFailure("AfterSave");

        command.CreatedOrderId = order.Id;
    }
}
