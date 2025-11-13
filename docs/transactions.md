# Transactional Command Pipeline

## Overview

The Transactional Command Pipeline provides automatic database transaction management for write commands in the Practical.CleanArchitecture solution. It ensures atomic writes, deterministic rollback on failure, and precise HTTP error mapping (409 vs 500) without polluting handlers with transaction logic.

## Architecture

### Components

1. **TransactionalCommandDecorator**: Decorator that wraps command handlers with transaction boundaries
2. **ITransactionalCommand**: Marker interface for commands requiring transactional behavior
3. **TransactionalAttribute**: Attribute-based approach for marking handlers (with configurable isolation level)
4. **TransactionalExceptionHandler**: Maps database exceptions to HTTP status codes (409/500)
5. **IFailureInjector**: Testing seam for simulating failures at specific points

### How It Works

```
┌──────────────────────────────────────────────────────────────┐
│                     API Controller                           │
└────────────────────────┬─────────────────────────────────────┘
                         │
                         │ Dispatcher.DispatchAsync(command)
                         ▼
┌──────────────────────────────────────────────────────────────┐
│              TransactionalCommandDecorator                   │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  1. Begin Transaction (IUnitOfWork)                    │  │
│  │  2. Start OpenTelemetry Span "command.transaction"    │  │
│  │  3. Call Inner Handler                                │  │
│  │  4. Commit Transaction (on success)                   │  │
│  │  5. Emit Telemetry (success/failure)                  │  │
│  │  6. Rollback (on exception via Dispose)               │  │
│  └────────────────────────────────────────────────────────┘  │
└────────────────────────┬─────────────────────────────────────┘
                         │
                         ▼
┌──────────────────────────────────────────────────────────────┐
│                  Command Handler                             │
│  - Business logic (HTTP-agnostic)                            │
│  - Calls repositories                                        │
│  - SaveChanges (within transaction boundary)                 │
└────────────────────────┬─────────────────────────────────────┘
                         │
                         │ On Exception
                         ▼
┌──────────────────────────────────────────────────────────────┐
│           TransactionalExceptionHandler                      │
│  - DbUpdateConcurrencyException → 409 ConcurrencyConflict    │
│  - DbUpdateException (unique) → 409 DuplicateDetected        │
│  - All other exceptions → 500 with correlationId             │
└──────────────────────────────────────────────────────────────┘
```

## Usage

### 1. Mark Command for Transactional Behavior

**Option A: Interface-based (Recommended)**
```csharp
public class CreateOrderCommand : ITransactionalCommand
{
    public Guid UserId { get; set; }
    public string ExternalOrderRef { get; set; }
    // ... other properties
}
```

**Option B: Attribute-based**
```csharp
[Transactional(IsolationLevel = IsolationLevel.ReadCommitted)]
internal class CreateOrderCommandHandler : ICommandHandler<CreateOrderCommand>
{
    // Handler implementation
}
```

### 2. Implement Handler (HTTP-agnostic)

```csharp
internal class CreateOrderCommandHandler : ICommandHandler<CreateOrderCommand>
{
    private readonly IRepository<Order, Guid> _orderRepository;
    private readonly IUnitOfWork _unitOfWork;

    public async Task HandleAsync(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        var order = new Order
        {
            UserId = command.UserId,
            ExternalOrderRef = command.ExternalOrderRef,
            // ... map properties
        };

        await _orderRepository.AddOrUpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Transaction is automatically committed by decorator
        // Rollback happens automatically on any exception
    }
}
```

### 3. Business Key Constraints

Define unique business keys in EF Core configuration:

```csharp
public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        // Unique constraint on business key
        builder.HasIndex(x => new { x.UserId, x.ExternalOrderRef })
            .IsUnique()
            .HasDatabaseName("IX_Orders_UserId_ExternalOrderRef_Unique");
    }
}
```

## HTTP Error Mapping

The `TransactionalExceptionHandler` maps exceptions to HTTP status codes:

| Exception Type | HTTP Status | Error Code | Use Case |
|----------------|-------------|------------|----------|
| `DbUpdateConcurrencyException` | 409 Conflict | `ConcurrencyConflict` | Optimistic concurrency violation (RowVersion) |
| `DbUpdateException` (unique constraint) | 409 Conflict | `DuplicateDetected` | Duplicate business key |
| All other exceptions | 500 Internal Server Error | Various | Unexpected errors |

### Example Response (409 Duplicate)
```json
{
  "status": 409,
  "title": "Duplicate Detected",
  "detail": "A resource with the same unique key already exists.",
  "type": "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.8",
  "code": "DuplicateDetected",
  "correlationId": "00-abc123...",
  "traceId": "xyz789..."
}
```

## Observability

### OpenTelemetry Spans

Every transactional command emits a span named `command.transaction` with attributes:

- `command`: Command type name (e.g., `CreateOrderCommand`)
- `correlation_id`: Unique request identifier
- `isolation_level`: Transaction isolation level
- `success`: Boolean indicating success/failure
- `rolled_back`: Boolean indicating if transaction was rolled back
- `error_code`: Error classification (e.g., `DuplicateDetected`, `UnexpectedError`)

### Logging

Structured logs include:
- `CorrelationId`: Trace requests across services
- `CommandName`: Which command was executed
- Transaction outcomes (started, committed, rolled back)

## Testing

### Unit Tests (Decorator)

Test the decorator in isolation using mocks:

```csharp
[Fact]
public async Task Given_TransactionalCommandSucceeds_When_Handled_Then_Commits_And_EmitsSuccessSpan()
{
    // Arrange
    var mockHandler = new Mock<ICommandHandler<TestCommand>>();
    var mockUnitOfWork = new Mock<IUnitOfWork>();
    // ... setup mocks

    // Act
    await decorator.HandleAsync(command, CancellationToken.None);

    // Assert
    mockUnitOfWork.Verify(x => x.CommitTransactionAsync(...), Times.Once);
}
```

### Integration Tests (Failure Injection)

Use `ConfigurableFailureInjector` to simulate failures at specific points:

```csharp
[Fact]
public async Task Given_UnexpectedFailure_BeforeSave_When_CreatingOrder_Then_NoPersistedRows()
{
    // Arrange
    var failureInjector = new ConfigurableFailureInjector();
    failureInjector.ConfigureFailure("BeforeSave", new Exception("Test failure"));

    // Inject into handler via DI
    var handler = new CreateOrderCommandHandler(..., failureInjector);

    // Act & Assert
    await Assert.ThrowsAsync<Exception>(() => decorator.HandleAsync(command));

    // Verify no rows in database
    var count = await dbContext.Orders.CountAsync();
    Assert.Equal(0, count);
}
```

### Injection Points

- `"BeforeSave"`: Before `SaveChangesAsync()` - simulates pre-persistence failure
- `"AfterSave"`: After `SaveChangesAsync()` - simulates post-persistence failure (still rolls back)

## Best Practices

1. **Use for write commands only**: Read queries should NOT be wrapped in transactions
2. **Single SaveChanges per command**: Multiple saves are allowed but stay within one transaction
3. **No nested transactions**: The decorator starts exactly one transaction
4. **HTTP-agnostic handlers**: Handlers should not know about HTTP status codes
5. **Business keys**: Use unique indexes for duplicate detection, not application logic
6. **Testing**: Always test both success and failure paths with rollback verification

## Design Decisions

### Why Decorator Pattern?
- **Separation of concerns**: Transaction logic separate from business logic
- **SOLID compliance**: Handlers remain focused on business rules
- **Testability**: Easy to mock and test in isolation

### Why Exception-based Mapping?
- **Centralized logic**: Single place to map exceptions to HTTP status codes
- **Consistency**: All commands use the same error mapping rules
- **Observability**: Exceptions are logged with correlation IDs

### Why Failure Injector?
- **Deterministic testing**: Simulate failures without complex test infrastructure
- **No production overhead**: No-op implementation has zero cost
- **Precise control**: Test exact failure scenarios (before/after save)

## Files Changed

```
src/Monolith/ClassifiedAds.Application/
├── Common/
│   ├── Commands/ITransactionalCommand.cs              (NEW)
│   └── Testing/
│       ├── IFailureInjector.cs                        (NEW)
│       └── NoOpFailureInjector.cs                     (NEW)
├── Decorators/Transactional/
│   ├── TransactionalAttribute.cs                      (NEW)
│   └── TransactionalCommandDecorator.cs               (NEW)
├── Orders/Commands/CreateOrderCommand.cs              (NEW)
└── ApplicationServicesExtensions.cs                   (MODIFIED)

src/Monolith/ClassifiedAds.Domain/
└── Entities/Order.cs                                  (NEW)

src/Monolith/ClassifiedAds.Persistence/
└── DbConfigurations/OrderConfiguration.cs             (NEW)

src/Monolith/ClassifiedAds.Infrastructure/
└── Web/ExceptionHandlers/TransactionalExceptionHandler.cs (NEW)

src/Monolith/ClassifiedAds.WebAPI/
└── Program.cs                                         (MODIFIED)

src/Monolith/ClassifiedAds.UnitTests/
├── Application/
│   ├── Decorators/TransactionalCommandDecoratorTests.cs   (NEW)
│   ├── Orders/CreateOrderCommandTransactionalTests.cs     (NEW)
│   └── Testing/ConfigurableFailureInjector.cs             (NEW)

docs/transactions.md                                   (NEW)
```

## References

- [EF Core Transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions)
- [ASP.NET Core ProblemDetails](https://learn.microsoft.com/en-us/aspnet/core/web-api/handle-errors)
- [OpenTelemetry .NET](https://opentelemetry.io/docs/instrumentation/net/)
