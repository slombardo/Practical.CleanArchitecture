namespace ClassifiedAds.Application;

/// <summary>
/// Marker interface for commands that require transactional behavior.
/// Commands implementing this interface will be wrapped in an EF Core transaction
/// that commits on success and rolls back on any exception.
/// </summary>
public interface ITransactionalCommand : ICommand
{
}
