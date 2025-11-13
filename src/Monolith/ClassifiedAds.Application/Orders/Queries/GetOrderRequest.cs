using ClassifiedAds.CrossCuttingConcerns.Exceptions;
using ClassifiedAds.Domain.Entities;
using ClassifiedAds.Domain.Repositories;
using MediatR;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Application.Orders.Queries;

public class GetOrderRequest : IRequest<Order>
{
    public Guid Id { get; set; }

    public bool ThrowNotFoundIfNull { get; set; }
}

internal class GetOrderRequestHandler : IRequestHandler<GetOrderRequest, Order>
{
    private readonly IRepository<Order, Guid> _orderRepository;

    public GetOrderRequestHandler(IRepository<Order, Guid> orderRepository)
    {
        _orderRepository = orderRepository;
    }

    public async Task<Order> Handle(GetOrderRequest request, CancellationToken cancellationToken)
    {
        var order = await _orderRepository.FirstOrDefaultAsync(
            _orderRepository.GetQueryableSet().Where(x => x.Id == request.Id));

        if (order == null && request.ThrowNotFoundIfNull)
        {
            throw new NotFoundException($"Order with Id {request.Id} not found.");
        }

        return order;
    }
}
