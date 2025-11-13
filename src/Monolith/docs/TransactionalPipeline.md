# MediatR Transactional Pipeline

This document describes the transactional command pipeline implemented using MediatR's `IPipelineBehavior<TRequest, TResponse>` pattern.

## Overview

The transactional pipeline provides:
- Automatic transaction wrapping for commands marked with `ITransactionalRequest`
- Deterministic rollback on any exception
- OpenTelemetry spans for observability
- HTTP error mapping for EF Core exceptions
- Failure injection for testing

## Architecture

```
Controller → IMediator.Send() → TransactionalBehavior → RequestHandler → Database
                                    ↓
                              BeginTransaction
                                    ↓
                               Execute Handler
                                    ↓
                              CommitTransaction (on success)
                                    or
                               Rollback (on exception)
```

## Key Components

### 1. ITransactionalRequest Marker Interface

Marks a request as requiring transactional wrapping:

```csharp
// ClassifiedAds.Application/Common/Behaviors/ITransactionalRequest.cs
public interface ITransactionalRequest { }
```

### 2. TransactionalBehavior<TRequest, TResponse>

MediatR pipeline behavior that wraps transactional requests:

```csharp
// ClassifiedAds.Application/Common/Behaviors/TransactionalBehavior.cs
public class TransactionalBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Skip non-transactional requests
        if (request is not ITransactionalRequest)
            return await next();

        // Wrap in transaction
        using (await _unitOfWork.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            var response = await next();
            await _unitOfWork.CommitTransactionAsync(cancellationToken);
            return response;
        }
        // Transaction automatically rolls back if not committed
    }
}
```

### 3. Registration

The behavior is registered in `ApplicationServicesExtensions.cs`:

```csharp
public static IServiceCollection AddMediatRWithTransactionalBehavior(this IServiceCollection services)
{
    services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly());
        cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(TransactionalBehavior<,>));
    });

    return services;
}
```

## Creating a Transactional Command

### 1. Define Request and Response

```csharp
// Request implements both IRequest<T> and ITransactionalRequest
public class CreateOrderRequest : IRequest<CreateOrderResponse>, ITransactionalRequest
{
    public Guid UserId { get; set; }
    public string ExternalOrderRef { get; set; }
    public string Description { get; set; }
    public decimal TotalAmount { get; set; }
}

public class CreateOrderResponse
{
    public Guid OrderId { get; set; }
}
```

### 2. Implement Handler

```csharp
internal class CreateOrderRequestHandler : IRequestHandler<CreateOrderRequest, CreateOrderResponse>
{
    private readonly IRepository<Order, Guid> _orderRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public async Task<CreateOrderResponse> Handle(
        CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        var order = new Order
        {
            UserId = request.UserId,
            ExternalOrderRef = request.ExternalOrderRef,
            Description = request.Description,
            TotalAmount = request.TotalAmount,
            Status = "Pending",
            CreatedDateTime = _dateTimeProvider.OffsetNow
        };

        await _orderRepository.AddOrUpdateAsync(order);
        await _orderRepository.UnitOfWork.SaveChangesAsync(cancellationToken);

        return new CreateOrderResponse { OrderId = order.Id };
    }
}
```

### 3. Use in Controller

```csharp
[HttpPost]
public async Task<ActionResult<OrderModel>> Post([FromBody] CreateOrderModel model)
{
    var request = new CreateOrderRequest
    {
        UserId = model.UserId,
        ExternalOrderRef = model.ExternalOrderRef,
        Description = model.Description,
        TotalAmount = model.TotalAmount
    };

    var response = await _mediator.Send(request);

    return Created($"/api/orders/{response.OrderId}", response);
}
```

## OpenTelemetry Observability

The behavior emits a `command.transaction` span with the following attributes:

| Attribute | Type | Description |
|-----------|------|-------------|
| `command` | string | Request type name (e.g., "CreateOrderRequest") |
| `success` | bool | Whether the transaction committed successfully |
| `rolled_back` | bool | Whether the transaction was rolled back |
| `error_code` | string | Error code on failure (correlates with HTTP error codes) |

### Error Code Mapping

The `error_code` attribute maps to HTTP response codes for easy correlation:

| Exception Type | error_code | HTTP Status | HTTP code field |
|----------------|------------|-------------|-----------------|
| `DbUpdateConcurrencyException` | `"ConcurrencyConflict"` | 409 Conflict | `"ConcurrencyConflict"` |
| `DbUpdateException` (unique constraint) | `"DuplicateDetected"` | 409 Conflict | `"DuplicateDetected"` |
| Other exceptions | Exception type name | 500 Internal Server Error | N/A |

## HTTP Error Handling

The `GlobalExceptionHandler` converts EF Core exceptions to appropriate HTTP responses:

### 409 Conflict - Concurrency Conflict

```json
{
  "status": 409,
  "title": "Conflict",
  "detail": "A concurrency conflict occurred. The resource was modified by another process.",
  "code": "ConcurrencyConflict",
  "correlationId": "trace-id-here"
}
```

### 409 Conflict - Duplicate Detected

```json
{
  "status": 409,
  "title": "Conflict",
  "detail": "A duplicate entry was detected. The resource already exists.",
  "code": "DuplicateDetected",
  "correlationId": "trace-id-here"
}
```

### 500 Internal Server Error

```json
{
  "status": 500,
  "title": "Internal Server Error",
  "detail": "An unexpected error occurred",
  "correlationId": "trace-id-here"
}
```

## Logging

The behavior logs:

**On Success:**
```
Information: Transaction committed for request {RequestName} with CorrelationId {CorrelationId}
```

**On Failure:**
```
Error: Transaction rolled back for request {RequestName} with CorrelationId {CorrelationId}. Error: {ErrorCode}
```

## Testing

### Unit Tests

Test the behavior in isolation using mocks:

```csharp
[Fact]
public async Task Given_TransactionalCommandFails_When_Handled_Then_LogsErrorWithCorrelationIdAndCommandName()
{
    // Arrange
    var request = new TestTransactionalRequest { Data = "test" };
    var expectedException = new InvalidOperationException("Database connection failed");
    RequestHandlerDelegate<TestResponse> next = () => throw expectedException;

    var mockLogger = new Mock<ILogger<TransactionalBehavior<TestTransactionalRequest, TestResponse>>>();
    var behavior = new TransactionalBehavior<TestTransactionalRequest, TestResponse>(
        _mockUnitOfWork.Object,
        mockLogger.Object);

    // Act
    await Assert.ThrowsAsync<InvalidOperationException>(
        () => behavior.Handle(request, next, CancellationToken.None));

    // Assert - error logged with command name and error code
    mockLogger.Verify(
        x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, type) =>
                state.ToString().Contains("TestTransactionalRequest") &&
                state.ToString().Contains("InvalidOperationException")),
            expectedException,
            It.IsAny<Func<It.IsAnyType, Exception, string>>()),
        Times.Once);
}
```

### Integration Tests with Failure Injection

The `IFailureInjector` interface allows deterministic failure testing:

```csharp
public interface IFailureInjector
{
    void CheckForFailure(string checkpointName);
    void ConfigureFailure(string checkpointName, Exception exception);
    void ClearFailures();
}
```

Usage in tests:

```csharp
[Fact]
public async Task Given_FailureAfterSave_When_CreatingOrder_Then_TransactionRolledBack_And_NoRowPersisted()
{
    // Arrange
    var injectedError = new InvalidOperationException("Simulated failure after save");
    _failureInjector.ConfigureFailure("AfterSave", injectedError);

    var request = new CreateOrderRequest { /* ... */ };

    // Act
    var exception = await Assert.ThrowsAsync<InvalidOperationException>(
        () => _mediator.Send(request));

    // Assert - no rows persisted (transaction rolled back)
    var ordersInDb = await _dbContext.Set<Order>().ToListAsync();
    Assert.Empty(ordersInDb);
}
```

### Testing Unique Constraint Violations

Use SQLite in-memory database (not EF Core's InMemory provider) to enforce unique constraints:

```csharp
_connection = new SqliteConnection("DataSource=:memory:");
_connection.Open();

services.AddDbContext<AdsDbContext>(options =>
    options.UseSqlite(_connection));

// SQLite enforces unique indexes defined in OrderConfiguration
```

## Business Key Enforcement

Unique constraints are defined in EF Core configuration:

```csharp
// ClassifiedAds.Persistence/DbConfigurations/OrderConfiguration.cs
public void Configure(EntityTypeBuilder<Order> builder)
{
    // Unique index on business key to prevent duplicate orders
    builder.HasIndex(x => new { x.UserId, x.ExternalOrderRef })
        .IsUnique()
        .HasDatabaseName("IX_Orders_UserId_ExternalOrderRef");
}
```

## Migration from Decorator Pattern

The codebase previously used a decorator pattern (`TransactionalCommandDecorator<TCommand>`). The MediatR behavior pattern offers:

1. **Better separation of concerns** - Pipeline behaviors are composable
2. **Standard pattern** - Well-known MediatR pattern
3. **Request/Response model** - Supports both commands and queries with responses
4. **Easier testing** - No decorator wrapping needed in tests

### Migration Steps

1. Create `ITransactionalRequest` marker interface
2. Implement `TransactionalBehavior<TRequest, TResponse>`
3. Update commands to implement both `IRequest<T>` and `ITransactionalRequest`
4. Register behavior with `AddMediatRWithTransactionalBehavior()`
5. Inject `IMediator` in controllers instead of `Dispatcher`
6. Use `_mediator.Send(request)` instead of dispatcher decorator wrapping

## Files Added/Modified

### New Files
- `ClassifiedAds.Application/Common/Behaviors/ITransactionalRequest.cs`
- `ClassifiedAds.Application/Common/Behaviors/TransactionalBehavior.cs`
- `ClassifiedAds.Application/Orders/Commands/CreateOrderRequest.cs`
- `ClassifiedAds.Application/Orders/Queries/GetOrderRequest.cs`
- `ClassifiedAds.UnitTests/Application/Behaviors/TransactionalBehaviorTests.cs`
- `ClassifiedAds.WebAPI.IntegrationTests/CreateOrderTransactionalTests.cs`
- `docs/TransactionalPipeline.md`

### Modified Files
- `ClassifiedAds.Application/ApplicationServicesExtensions.cs` - Added `AddMediatRWithTransactionalBehavior()`
- `ClassifiedAds.Application/ClassifiedAds.Application.csproj` - Added MediatR package
- `ClassifiedAds.WebAPI/Program.cs` - Registered MediatR behavior
- `ClassifiedAds.WebAPI/Controllers/OrdersController.cs` - Uses `IMediator`
- `ClassifiedAds.Infrastructure/Web/ExceptionHandlers/GlobalExceptionHandler.cs` - 409/500 HTTP mapping

## Best Practices

1. **Mark only write operations** as `ITransactionalRequest` - queries don't need transactions
2. **Keep handlers focused** - Each handler does one thing within the transaction
3. **Use business keys** - Define unique indexes to prevent duplicates at the database level
4. **Monitor spans** - Use the `command.transaction` span to track transaction success/failure rates
5. **Correlate logs with traces** - Use the `correlationId` to link HTTP responses back to traces
