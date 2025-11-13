using ClassifiedAds.CrossCuttingConcerns.Exceptions;
using ClassifiedAds.Domain.Entities;
using ClassifiedAds.Domain.Repositories;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Application.Orders.Queries;

public class GetOrderQuery : IQuery<Order>
{
    public Guid Id { get; set; }

    public bool ThrowNotFoundIfNull { get; set; }
}

internal class GetOrderQueryHandler : IQueryHandler<GetOrderQuery, Order>
{
    private readonly IRepository<Order, Guid> _orderRepository;

    public GetOrderQueryHandler(IRepository<Order, Guid> orderRepository)
    {
        _orderRepository = orderRepository;
    }

    public async Task<Order> HandleAsync(GetOrderQuery query, CancellationToken cancellationToken = default)
    {
        var order = await _orderRepository.FirstOrDefaultAsync(
            _orderRepository.GetQueryableSet().Where(x => x.Id == query.Id));

        if (order == null && query.ThrowNotFoundIfNull)
        {
            throw new NotFoundException($"Order with Id {query.Id} not found.");
        }

        return order;
    }
}
