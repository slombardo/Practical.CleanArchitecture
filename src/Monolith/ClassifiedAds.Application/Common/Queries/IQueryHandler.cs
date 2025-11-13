using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Application;

/// <summary>
/// Legacy query handler interface.
/// </summary>
[Obsolete("IQueryHandler is being replaced by MediatR.IRequestHandler. New handlers should implement IRequestHandler<TRequest, TResponse> from MediatR. This will be removed in a future version.")]
public interface IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}
