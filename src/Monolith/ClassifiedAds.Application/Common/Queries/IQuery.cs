using System;

namespace ClassifiedAds.Application;

/// <summary>
/// Legacy query marker interface.
/// </summary>
[Obsolete("IQuery is being replaced by MediatR.IRequest<TResponse>. New queries should implement IRequest<TResponse> from MediatR. This will be removed in a future version.")]
public interface IQuery<TResult>
{
}
