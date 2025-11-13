using System;

namespace ClassifiedAds.Domain.Entities;

/// <summary>
/// Order entity with a composite business key (UserId, ExternalOrderRef) to enforce uniqueness.
/// </summary>
public class Order : Entity<Guid>, IAggregateRoot
{
    /// <summary>
    /// The user who placed the order.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// External reference provided by the client to uniquely identify the order.
    /// Combined with UserId, this forms the business key that prevents duplicates.
    /// </summary>
    public string ExternalOrderRef { get; set; }

    /// <summary>
    /// Description of the order.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Total amount of the order.
    /// </summary>
    public decimal TotalAmount { get; set; }

    /// <summary>
    /// Status of the order (e.g., "Pending", "Confirmed", "Shipped").
    /// </summary>
    public string Status { get; set; }
}
