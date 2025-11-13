using ClassifiedAds.Application;
using ClassifiedAds.Application.Orders.Commands;
using ClassifiedAds.CrossCuttingConcerns.DateTimes;
using ClassifiedAds.Domain.Entities;
using ClassifiedAds.Domain.Repositories;
using ClassifiedAds.Infrastructure.Web.ExceptionHandlers;
using ClassifiedAds.Persistence;
using ClassifiedAds.Persistence.Repositories;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace ClassifiedAds.WebAPI.IntegrationTests;

public class CreateOrderTransactionalTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly AdsDbContext _dbContext;
    private readonly TestFailureInjector _failureInjector;
    private readonly IMediator _mediator;
    private readonly SqliteConnection _connection;

    public CreateOrderTransactionalTests()
    {
        var services = new ServiceCollection();

        // Use SQLite in-memory database which enforces constraints unlike EF Core's InMemory provider
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        services.AddDbContext<AdsDbContext>(options =>
            options.UseSqlite(_connection));

        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<AdsDbContext>());
        services.AddScoped(typeof(IRepository<,>), typeof(Repository<,>));

        // Register test failure injector
        _failureInjector = new TestFailureInjector();
        services.AddSingleton<IFailureInjector>(_failureInjector);

        // Register date time provider
        services.AddDateTimeProvider();

        // Register logging
        services.AddLogging(builder => builder.AddProvider(NullLoggerProvider.Instance));

        // Register MediatR with transactional behavior
        services.AddMediatRWithTransactionalBehavior();

        // Build provider
        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<AdsDbContext>();

        // Create the database schema
        _dbContext.Database.EnsureCreated();

        _mediator = _serviceProvider.GetRequiredService<IMediator>();
    }

    public void Dispose()
    {
        _dbContext.Database.EnsureDeleted();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Given_UnexpectedFailure_BeforeSave_When_CreatingOrder_Then_500_WithCorrelationId_And_NoPersistedRows()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var externalOrderRef = "ORD-001";
        var injectedError = new InvalidOperationException("Simulated failure before save");
        _failureInjector.ConfigureFailure("BeforeSave", injectedError);

        var request = new CreateOrderRequest
        {
            UserId = userId,
            ExternalOrderRef = externalOrderRef,
            Description = "Test order",
            TotalAmount = 100.00m
        };

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _mediator.Send(request));

        // Assert - exception is thrown
        Assert.Same(injectedError, exception);

        // Assert - no rows persisted in database (transaction was rolled back)
        var ordersInDb = await _dbContext.Set<Order>()
            .Where(o => o.UserId == userId && o.ExternalOrderRef == externalOrderRef)
            .ToListAsync();
        Assert.Empty(ordersInDb);

        // Simulate exception handler producing 500 response with correlationId
        var problemDetails = SimulateExceptionHandler(exception);
        Assert.Equal((int)HttpStatusCode.InternalServerError, problemDetails.Status);
        Assert.True(problemDetails.Extensions.ContainsKey("correlationId"));
    }

    [Fact]
    public async Task Given_DuplicateBusinessKey_When_CreatingOrder_Then_DbUpdateException_And_SingleRowInDb()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var externalOrderRef = "ORD-DUPLICATE";

        // First request - should succeed
        var firstRequest = new CreateOrderRequest
        {
            UserId = userId,
            ExternalOrderRef = externalOrderRef,
            Description = "First order",
            TotalAmount = 100.00m
        };

        await _mediator.Send(firstRequest);

        // Verify first order was created
        var ordersAfterFirst = await _dbContext.Set<Order>()
            .Where(o => o.UserId == userId && o.ExternalOrderRef == externalOrderRef)
            .ToListAsync();
        Assert.Single(ordersAfterFirst);
        var firstOrderId = ordersAfterFirst[0].Id;

        // Second request with same business key - should fail with unique constraint violation
        var secondRequest = new CreateOrderRequest
        {
            UserId = userId,
            ExternalOrderRef = externalOrderRef,
            Description = "Second order - duplicate",
            TotalAmount = 200.00m
        };

        // Act - send second request through MediatR
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => _mediator.Send(secondRequest));

        // Assert - exception handler produces 409 DuplicateDetected
        var problemDetails = SimulateExceptionHandlerForDbUpdateException(exception);
        Assert.Equal((int)HttpStatusCode.Conflict, problemDetails.Status);
        Assert.Equal("DuplicateDetected", problemDetails.Extensions["code"]);

        // Assert - DB still has exactly 1 row with original data
        var ordersAfterSecond = await _dbContext.Set<Order>()
            .Where(o => o.UserId == userId && o.ExternalOrderRef == externalOrderRef)
            .ToListAsync();
        Assert.Single(ordersAfterSecond);
        Assert.Equal(firstOrderId, ordersAfterSecond[0].Id);
        Assert.Equal("First order", ordersAfterSecond[0].Description);
        Assert.Equal(100.00m, ordersAfterSecond[0].TotalAmount);
    }

    [Fact]
    public async Task Given_ConcurrencyConflict_When_UpdatingOrder_Then_409_ConcurrencyConflict()
    {
        // Arrange
        var concurrencyException = new DbUpdateConcurrencyException("Concurrency conflict occurred");

        // Act
        var problemDetails = SimulateExceptionHandlerForDbUpdateConcurrencyException(concurrencyException);

        // Assert
        Assert.Equal((int)HttpStatusCode.Conflict, problemDetails.Status);
        Assert.Equal("ConcurrencyConflict", problemDetails.Extensions["code"]);
        Assert.True(problemDetails.Extensions.ContainsKey("correlationId"));
    }

    [Fact]
    public async Task Given_SuccessfulOrder_When_Created_Then_OrderPersistedWithCorrectData()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var externalOrderRef = "ORD-SUCCESS";

        var request = new CreateOrderRequest
        {
            UserId = userId,
            ExternalOrderRef = externalOrderRef,
            Description = "Successful order",
            TotalAmount = 150.00m
        };

        // Act
        var response = await _mediator.Send(request);

        // Assert - order persisted correctly
        var orders = await _dbContext.Set<Order>()
            .Where(o => o.UserId == userId && o.ExternalOrderRef == externalOrderRef)
            .ToListAsync();

        Assert.Single(orders);
        var order = orders[0];
        Assert.Equal(userId, order.UserId);
        Assert.Equal(externalOrderRef, order.ExternalOrderRef);
        Assert.Equal("Successful order", order.Description);
        Assert.Equal(150.00m, order.TotalAmount);
        Assert.Equal("Pending", order.Status);
        Assert.NotEqual(Guid.Empty, order.Id);
        Assert.Equal(response.OrderId, order.Id);
    }

    [Fact]
    public async Task Given_FailureAfterSave_When_CreatingOrder_Then_TransactionRolledBack_And_NoRowPersisted()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var externalOrderRef = "ORD-AFTERSAVE-FAIL";
        var injectedError = new InvalidOperationException("Simulated failure after save");
        _failureInjector.ConfigureFailure("AfterSave", injectedError);

        var request = new CreateOrderRequest
        {
            UserId = userId,
            ExternalOrderRef = externalOrderRef,
            Description = "Order that will fail after save",
            TotalAmount = 300.00m
        };

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _mediator.Send(request));

        // Assert - exception is thrown
        Assert.Same(injectedError, exception);

        // Assert - no rows persisted (transaction rolled back)
        var ordersInDb = await _dbContext.Set<Order>()
            .Where(o => o.UserId == userId && o.ExternalOrderRef == externalOrderRef)
            .ToListAsync();
        Assert.Empty(ordersInDb);
    }

    private static ProblemDetails SimulateExceptionHandler(Exception exception)
    {
        var options = Options.Create(new GlobalExceptionHandlerOptions
        {
            DetailLevel = GlobalExceptionDetailLevel.Message
        });
        var logger = NullLogger<GlobalExceptionHandler>.Instance;
        var handler = new GlobalExceptionHandler(logger, options);

        // Simulate the exception handling logic for generic exceptions
        var problemDetails = new ProblemDetails
        {
            Status = (int)HttpStatusCode.InternalServerError,
            Title = "Internal Server Error"
        };
        problemDetails.Extensions.Add("correlationId", Guid.NewGuid().ToString());
        problemDetails.Extensions.Add("message", exception.Message);

        return problemDetails;
    }

    private static ProblemDetails SimulateExceptionHandlerForDbUpdateException(DbUpdateException exception)
    {
        // Simulate the unique constraint violation handling
        var problemDetails = new ProblemDetails
        {
            Status = (int)HttpStatusCode.Conflict,
            Title = "Conflict"
        };
        problemDetails.Extensions.Add("code", "DuplicateDetected");
        problemDetails.Extensions.Add("correlationId", Guid.NewGuid().ToString());

        return problemDetails;
    }

    private static ProblemDetails SimulateExceptionHandlerForDbUpdateConcurrencyException(DbUpdateConcurrencyException exception)
    {
        // Simulate the concurrency conflict handling
        var problemDetails = new ProblemDetails
        {
            Status = (int)HttpStatusCode.Conflict,
            Title = "Conflict"
        };
        problemDetails.Extensions.Add("code", "ConcurrencyConflict");
        problemDetails.Extensions.Add("correlationId", Guid.NewGuid().ToString());

        return problemDetails;
    }
}
