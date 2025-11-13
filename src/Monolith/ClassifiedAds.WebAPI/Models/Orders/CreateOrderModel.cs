using System;

namespace ClassifiedAds.WebAPI.Models.Orders;

public class CreateOrderModel
{
    public Guid UserId { get; set; }

    public string ExternalOrderRef { get; set; }

    public string Description { get; set; }

    public decimal TotalAmount { get; set; }
}
