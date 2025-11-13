namespace ClassifiedAds.Application.Common.Behaviors;

/// <summary>
/// Marker interface for MediatR requests that require transactional behavior.
/// Requests implementing this interface will be wrapped in an EF Core transaction
/// by the TransactionalBehavior pipeline.
/// </summary>
public interface ITransactionalRequest
{
}
