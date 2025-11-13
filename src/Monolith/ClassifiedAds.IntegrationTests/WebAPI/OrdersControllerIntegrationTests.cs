using ClassifiedAds.Application.Orders.Commands;
using ClassifiedAds.Domain.Entities;
using ClassifiedAds.IntegrationTests.Helpers;
using ClassifiedAds.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace ClassifiedAds.IntegrationTests.WebAPI;

/// <summary>
/// End-to-end integration tests for the CreateOrder endpoint.
/// Tests the full HTTP → MediatR → TransactionalBehavior → Handler → Database flow.
/// </summary>
public class OrdersControllerIntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
    private readonly TestWebApplicationFactory<Program> _factory;

    public OrdersControllerIntegrationTests(TestWebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Test 1: Force failure before SaveChanges → Assert 500 + correlationId + no rows.
    /// </summary>
    [Fact]
    public async Task Given_FailureBeforeSave_When_CreatingOrder_Then_Returns500WithCorrelationId_And_NoRowsPersisted()
    {
        // Arrange - Configure failure injection
        var failureInjector = new ConfigurableFailureInjector();
        failureInjector.ConfigureFailure("BeforeSave", new InvalidOperationException("Test failure before save"));

        _factory.ConfigureFailureInjector(failureInjector);
        var client = _factory.CreateClient();

        var userId = Guid.NewGuid();
        var externalOrderRef = $"TEST-ORDER-{Guid.NewGuid()}";

        var command = new CreateOrderCommand
        {
            UserId = userId,
            ExternalOrderRef = externalOrderRef,
            TotalAmount = 100.50m,
            Currency = "USD",
            Notes = "Test order"
        };

        // Act - Send POST request
        var response = await client.PostAsJsonAsync("/api/orders", command);

        // Assert - Verify 500 response
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // Assert - Verify correlationId exists in ProblemDetails response
        var content = await response.Content.ReadAsStringAsync();
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.True(problemDetails.TryGetProperty("correlationId", out var correlationIdElement));
        var correlationId = correlationIdElement.GetString();
        Assert.False(string.IsNullOrWhiteSpace(correlationId), "correlationId should not be empty");

        // Assert - Verify NO rows were persisted in database (transaction rolled back)
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AdsDbContext>();
        var persistedOrders = await dbContext.Set<Order>()
            .Where(o => o.UserId == userId && o.ExternalOrderRef == externalOrderRef)
            .ToListAsync();

        Assert.Empty(persistedOrders);
    }

    /// <summary>
    /// Test 2: Send same business key twice → Assert first succeeds, second returns 409 DuplicateDetected with exactly one row.
    /// </summary>
    [Fact]
    public async Task Given_DuplicateBusinessKey_When_CreatingOrderTwice_Then_FirstSucceeds_SecondReturns409_WithOneRow()
    {
        // Arrange - Use no-op failure injector (normal behavior)
        _factory.ConfigureFailureInjector(new NoOpFailureInjector());
        var client = _factory.CreateClient();

        var userId = Guid.NewGuid();
        var externalOrderRef = $"UNIQUE-ORDER-{Guid.NewGuid()}";

        var command = new CreateOrderCommand
        {
            UserId = userId,
            ExternalOrderRef = externalOrderRef,
            TotalAmount = 250.00m,
            Currency = "EUR",
            Notes = "Duplicate test"
        };

        // Act - First request should succeed
        var firstResponse = await client.PostAsJsonAsync("/api/orders", command);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        // Act - Second request with same business key should fail with 409
        var secondResponse = await client.PostAsJsonAsync("/api/orders", command);

        // Assert - Verify 409 Conflict response
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);

        // Assert - Verify ProblemDetails contains "DuplicateDetected" error code
        var content = await secondResponse.Content.ReadAsStringAsync();
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.True(problemDetails.TryGetProperty("code", out var codeElement));
        var errorCode = codeElement.GetString();
        Assert.Equal("DuplicateDetected", errorCode);

        // Assert - Verify correlationId exists for traceability
        Assert.True(problemDetails.TryGetProperty("correlationId", out var correlationIdElement));
        var correlationId = correlationIdElement.GetString();
        Assert.False(string.IsNullOrWhiteSpace(correlationId), "correlationId should not be empty");

        // Assert - Verify exactly ONE row exists in database (from first request only)
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AdsDbContext>();
        var persistedOrders = await dbContext.Set<Order>()
            .Where(o => o.UserId == userId && o.ExternalOrderRef == externalOrderRef)
            .ToListAsync();

        Assert.Single(persistedOrders);
        var order = persistedOrders.First();
        Assert.Equal(userId, order.UserId);
        Assert.Equal(externalOrderRef, order.ExternalOrderRef);
        Assert.Equal(250.00m, order.TotalAmount);
        Assert.Equal("EUR", order.Currency);
    }

    /// <summary>
    /// No-op failure injector for tests that don't need failure injection.
    /// </summary>
    private class NoOpFailureInjector : ClassifiedAds.Application.Common.Testing.IFailureInjector
    {
        public Task InjectAsync(string injectionPoint) => Task.CompletedTask;
    }
}
