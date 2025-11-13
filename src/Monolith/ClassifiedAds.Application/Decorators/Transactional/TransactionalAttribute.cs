using System;

namespace ClassifiedAds.Application.Decorators.Transactional;

/// <summary>
/// Attribute to mark commands for transactional behavior.
/// Commands decorated with this attribute will be wrapped in a database transaction,
/// ensuring atomic writes and deterministic rollback on failure.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class TransactionalAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the isolation level for the transaction.
    /// Default is ReadCommitted.
    /// </summary>
    public System.Data.IsolationLevel IsolationLevel { get; set; } = System.Data.IsolationLevel.ReadCommitted;
}
