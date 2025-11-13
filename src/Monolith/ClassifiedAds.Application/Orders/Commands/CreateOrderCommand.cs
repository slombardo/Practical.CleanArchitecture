using ClassifiedAds.Application.Common.Commands;
using ClassifiedAds.Application.Common.Testing;
using ClassifiedAds.Application.Decorators.Transactional;
using ClassifiedAds.Domain.Entities;
using ClassifiedAds.Domain.Repositories;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Application.Orders.Commands;

/// <summary>
/// Command to create a new order.
/// Implements ITransactionalCommand to ensure atomic writes and deterministic rollback.
/// Uses unique business key (UserId + ExternalOrderRef) to prevent duplicates.
/// </summary>
public class CreateOrderCommand : ITransactionalCommand
{
    public Guid UserId { get; set; }
    public string ExternalOrderRef { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; }
    public string Notes { get; set; }
}

/// <summary>
/// Handler for CreateOrderCommand.
/// Creates a new order with transactional guarantees.
/// Uses IFailureInjector for testing failure scenarios.
/// </summary>
[Transactional(IsolationLevel = System.Data.IsolationLevel.ReadCommitted)]
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
        // Generate order number
        var orderNumber = GenerateOrderNumber();

        // Create order entity
        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = command.UserId,
            ExternalOrderRef = command.ExternalOrderRef,
            OrderNumber = orderNumber,
            TotalAmount = command.TotalAmount,
            Currency = command.Currency,
            Status = "Pending",
            Notes = command.Notes,
            CreatedDateTime = DateTimeOffset.UtcNow
        };

        // Add to repository
        await _orderRepository.AddOrUpdateAsync(order, cancellationToken);

        // Failure injection point for testing (no-op in production)
        await _failureInjector.InjectAsync("BeforeSave");

        // Save changes (within the transaction boundary managed by TransactionalCommandDecorator)
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Failure injection point after save (to test transaction rollback)
        await _failureInjector.InjectAsync("AfterSave");
    }

    private string GenerateOrderNumber()
    {
        // Generate a unique order number with timestamp and random component
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var random = new Random().Next(1000, 9999);
        return $"ORD-{timestamp}-{random}";
    }
}
