using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Application;

[Obsolete("ICommandHandler<T> is deprecated. Use MediatR IRequestHandler<TRequest, TResponse> instead. See docs/MediatRMigrationStrategy.md for migration guidance and docs/adr/0001-migrate-to-mediatr-from-decorator-pattern.md for rationale.")]
public interface ICommandHandler<TCommand>
    where TCommand : ICommand
{
    Task HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}
