namespace ClassifiedAds.Application.Common.Commands;

/// <summary>
/// Marker interface for commands that require transactional behavior.
/// Commands implementing this interface will be wrapped in a database transaction
/// by the TransactionalCommandDecorator, ensuring atomic writes and deterministic rollback on failure.
/// </summary>
public interface ITransactionalCommand : ICommand
{
}
