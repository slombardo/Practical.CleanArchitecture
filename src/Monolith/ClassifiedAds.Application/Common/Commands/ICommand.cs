using System;

namespace ClassifiedAds.Application;

[Obsolete("ICommand is deprecated. Use MediatR IRequest<TResponse> instead. See docs/MediatRMigrationStrategy.md for migration guidance and docs/adr/0001-migrate-to-mediatr-from-decorator-pattern.md for rationale.")]
public interface ICommand
{
}
