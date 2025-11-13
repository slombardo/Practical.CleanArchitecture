using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Application;

[Obsolete("IQueryHandler<TQuery, TResult> is deprecated. Use MediatR IRequestHandler<TRequest, TResponse> instead. See docs/MediatRMigrationStrategy.md for migration guidance and docs/adr/0001-migrate-to-mediatr-from-decorator-pattern.md for rationale.")]
public interface IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}
