using ClassifiedAds.Application.Orders.Commands;
using ClassifiedAds.Application.Orders.Queries;
using ClassifiedAds.Domain.Entities;
using ClassifiedAds.WebAPI.Models.Orders;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace ClassifiedAds.WebAPI.Controllers;

[Authorize]
[Produces("application/json")]
[Route("api/[controller]")]
[ApiController]
public class OrdersController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(IMediator mediator, ILogger<OrdersController> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderModel>> Get(Guid id)
    {
        var order = await _mediator.Send(new GetOrderRequest { Id = id, ThrowNotFoundIfNull = true });
        var model = ToModel(order);
        return Ok(model);
    }

    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderModel>> Post([FromBody] CreateOrderModel model)
    {
        _logger.LogInformation(
            "Creating order for UserId {UserId} with ExternalOrderRef {ExternalOrderRef}",
            model.UserId,
            model.ExternalOrderRef);

        var request = new CreateOrderRequest
        {
            UserId = model.UserId,
            ExternalOrderRef = model.ExternalOrderRef,
            Description = model.Description,
            TotalAmount = model.TotalAmount
        };

        var response = await _mediator.Send(request);

        // Fetch the created order to return full details
        var order = await _mediator.Send(new GetOrderRequest { Id = response.OrderId, ThrowNotFoundIfNull = true });
        var resultModel = ToModel(order);

        return Created($"/api/orders/{resultModel.Id}", resultModel);
    }

    private static OrderModel ToModel(Order order)
    {
        return new OrderModel
        {
            Id = order.Id,
            UserId = order.UserId,
            ExternalOrderRef = order.ExternalOrderRef,
            Description = order.Description,
            TotalAmount = order.TotalAmount,
            Status = order.Status,
            CreatedDateTime = order.CreatedDateTime,
            UpdatedDateTime = order.UpdatedDateTime
        };
    }
}
