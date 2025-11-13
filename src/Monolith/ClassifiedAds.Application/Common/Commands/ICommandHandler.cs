using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClassifiedAds.Application;

/// <summary>
/// Legacy command handler interface.
/// </summary>
[Obsolete("ICommandHandler is being replaced by MediatR.IRequestHandler. New handlers should implement IRequestHandler<TRequest, TResponse> from MediatR. This will be removed in a future version.")]
public interface ICommandHandler<TCommand>
    where TCommand : ICommand
{
    Task HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}
