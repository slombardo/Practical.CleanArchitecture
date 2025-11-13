using System;

namespace ClassifiedAds.WebAPI.Models.Orders;

public class OrderModel
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string ExternalOrderRef { get; set; }

    public string Description { get; set; }

    public decimal TotalAmount { get; set; }

    public string Status { get; set; }

    public DateTimeOffset CreatedDateTime { get; set; }

    public DateTimeOffset? UpdatedDateTime { get; set; }
}
