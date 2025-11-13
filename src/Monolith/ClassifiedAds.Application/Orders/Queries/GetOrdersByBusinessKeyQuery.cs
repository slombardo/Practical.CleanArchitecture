using ClassifiedAds.Domain.Entities;
using ClassifiedAds.Domain.Repositories;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Application.Orders.Queries;

public class GetOrdersByBusinessKeyQuery : IQuery<List<Order>>
{
    public Guid UserId { get; set; }

    public string ExternalOrderRef { get; set; }
}

internal class GetOrdersByBusinessKeyQueryHandler : IQueryHandler<GetOrdersByBusinessKeyQuery, List<Order>>
{
    private readonly IRepository<Order, Guid> _orderRepository;

    public GetOrdersByBusinessKeyQueryHandler(IRepository<Order, Guid> orderRepository)
    {
        _orderRepository = orderRepository;
    }

    public async Task<List<Order>> HandleAsync(GetOrdersByBusinessKeyQuery query, CancellationToken cancellationToken = default)
    {
        return await _orderRepository.ToListAsync(
            _orderRepository.GetQueryableSet()
                .Where(x => x.UserId == query.UserId && x.ExternalOrderRef == query.ExternalOrderRef));
    }
}
