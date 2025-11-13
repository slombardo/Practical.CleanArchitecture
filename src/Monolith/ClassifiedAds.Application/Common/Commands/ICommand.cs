using System;

namespace ClassifiedAds.Application;

/// <summary>
/// Legacy command marker interface.
/// </summary>
[Obsolete("ICommand is being replaced by MediatR.IRequest. New commands should implement IRequest or IRequest<TResponse> from MediatR. This will be removed in a future version.")]
public interface ICommand
{
}
