# Transactional Command Pipeline

## Overview

The Transactional Command Pipeline provides automatic database transaction management for write commands in the Practical.CleanArchitecture solution using **MediatR's `IPipelineBehavior<TRequest,TResponse>`**. It ensures atomic writes, deterministic rollback on failure, and precise HTTP error mapping (409 vs 500) without polluting handlers with transaction logic.

## Architecture

### Components

1. **TransactionalBehavior**: MediatR pipeline behavior that wraps command handlers with transaction boundaries
2. **ITransactionalCommand**: Marker interface for commands requiring transactional behavior
3. **TransactionalExceptionHandler**: Maps database exceptions to HTTP status codes (409/500)
4. **IFailureInjector**: Testing seam for simulating failures at specific points
5. **GlobalExceptionHandler**: Catches all unhandled exceptions and returns 500 with correlationId

### How It Works

```
┌──────────────────────────────────────────────────────────────┐
│                     API Controller                           │
└────────────────────────┬─────────────────────────────────────┘
                         │
                         │ IMediator.Send(command)
                         ▼
┌──────────────────────────────────────────────────────────────┐
│              MediatR Pipeline (IPipelineBehavior)            │
│  ┌────────────────────────────────────────────────────────┐  │
│  │           TransactionalBehavior<TRequest,TResponse>    │  │
│  │  1. Check if command implements ITransactionalCommand │  │
│  │  2. Begin Transaction (IUnitOfWork)                   │  │
│  │  3. Start OpenTelemetry Span "command.transaction"   │  │
│  │  4. Call next() → Inner Handler                      │  │
│  │  5. Commit Transaction (on success)                  │  │
│  │  6. Emit Telemetry (success/failure)                 │  │
│  │  7. Rollback (on exception via Dispose)              │  │
│  └────────────────────────────────────────────────────────┘  │
└────────────────────────┬─────────────────────────────────────┘
                         │
                         ▼
┌──────────────────────────────────────────────────────────────┐
│        Command Handler (IRequestHandler<TRequest>)           │
│  - Business logic (HTTP-agnostic)                            │
│  - Calls repositories                                        │
│  - SaveChanges (within transaction boundary)                 │
└────────────────────────┬─────────────────────────────────────┘
                         │
                         │ On Exception
                         ▼
┌──────────────────────────────────────────────────────────────┐
│           TransactionalExceptionHandler (FIRST)              │
│  - DbUpdateConcurrencyException → 409 ConcurrencyConflict    │
│  - DbUpdateException (unique) → 409 DuplicateDetected        │
│  - Returns false for all others → falls through              │
└────────────────────────┬─────────────────────────────────────┘
                         │ (if not handled)
                         ▼
┌──────────────────────────────────────────────────────────────┐
│           GlobalExceptionHandler (FALLBACK)                  │
│  - All other exceptions → 500 with correlationId + logs      │
└──────────────────────────────────────────────────────────────┘
```

## Usage

### 1. Mark Command for Transactional Behavior (MediatR)

Commands must implement **both** `IRequest` (or `IRequest<TResponse>`) from MediatR **and** `ITransactionalCommand`:

```csharp
using MediatR;
using ClassifiedAds.Application.Common.Commands;

public class CreateOrderCommand : IRequest, ITransactionalCommand
{
    public Guid UserId { get; set; }
    public string ExternalOrderRef { get; set; }
    // ... other properties
}
```

**Key Points:**
- `IRequest` / `IRequest<TResponse>` - Required by MediatR
- `ITransactionalCommand` - Marker interface detected by `TransactionalBehavior`
- No attributes needed - MediatR handles everything via pipeline

### 2. Implement Handler (HTTP-agnostic)

```csharp
using MediatR;

internal class CreateOrderCommandHandler : IRequestHandler<CreateOrderCommand>
{
    private readonly IRepository<Order, Guid> _orderRepository;
    private readonly IUnitOfWork _unitOfWork;

    public async Task Handle(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        var order = new Order
        {
            UserId = command.UserId,
            ExternalOrderRef = command.ExternalOrderRef,
            // ... map properties
        };

        await _orderRepository.AddOrUpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Transaction is automatically committed by MediatR pipeline behavior
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

### Why MediatR Pipeline Behavior?
- **Industry standard**: MediatR is the de facto standard for CQRS in .NET
- **Single source of truth**: One pipeline for all cross-cutting concerns
- **Separation of concerns**: Transaction logic separate from business logic
- **SOLID compliance**: Handlers remain focused on business rules
- **Testability**: Easy to mock and test behaviors in isolation
- **Composition**: Behaviors can be stacked and ordered

### Why Exception-based Mapping?
- **Centralized logic**: Exception handlers map exceptions to HTTP status codes
- **Consistency**: All commands use the same error mapping rules
- **Chain of responsibility**: TransactionalExceptionHandler → GlobalExceptionHandler
- **Observability**: Exceptions are logged with correlationId for tracing

### Why Failure Injector?
- **Deterministic testing**: Simulate failures without complex test infrastructure
- **No production overhead**: No-op implementation has zero cost
- **Precise control**: Test exact failure scenarios (before/after save)

## Files Changed

```
src/Monolith/ClassifiedAds.Application/
├── Common/
│   ├── Behaviors/TransactionalBehavior.cs             (NEW - MediatR IPipelineBehavior)
│   ├── Commands/ITransactionalCommand.cs              (NEW)
│   └── Testing/
│       ├── IFailureInjector.cs                        (NEW)
│       └── NoOpFailureInjector.cs                     (NEW)
├── Orders/Commands/CreateOrderCommand.cs              (NEW - implements IRequest + ITransactionalCommand)
├── ApplicationServicesExtensions.cs                   (MODIFIED - registered MediatR)
└── ClassifiedAds.Application.csproj                   (MODIFIED - added MediatR package)

src/Monolith/ClassifiedAds.Domain/
└── Entities/Order.cs                                  (NEW)

src/Monolith/ClassifiedAds.Persistence/
└── DbConfigurations/OrderConfiguration.cs             (NEW)

src/Monolith/ClassifiedAds.Infrastructure/
├── Web/ExceptionHandlers/
│   ├── TransactionalExceptionHandler.cs               (NEW - handles 409 cases)
│   └── GlobalExceptionHandler.cs                      (MODIFIED - added correlationId for 500)

src/Monolith/ClassifiedAds.WebAPI/
└── Program.cs                                         (MODIFIED - registered TransactionalExceptionHandler)

src/Monolith/ClassifiedAds.UnitTests/
├── Application/
│   ├── Behaviors/TransactionalBehaviorTests.cs        (NEW - MediatR behavior tests)
│   └── Testing/ConfigurableFailureInjector.cs         (NEW)

docs/transactions.md                                   (NEW)
```

## Migration from Legacy Dispatcher

### Obsolete Infrastructure

The following components are marked as `[Obsolete]` and will be removed in a future version:

- **`Dispatcher`**: Custom command/query dispatcher replaced by MediatR
- **`ICommand`**: Legacy command marker interface → Use `IRequest` or `IRequest<TResponse>` from MediatR
- **`ICommandHandler<TCommand>`**: Legacy handler interface → Use `IRequestHandler<TRequest, TResponse>` from MediatR
- **`IQuery<TResult>`**: Legacy query marker interface → Use `IRequest<TResponse>` from MediatR
- **`IQueryHandler<TQuery, TResult>`**: Legacy handler interface → Use `IRequestHandler<TRequest, TResponse>` from MediatR

### Migration Path

**Before (Legacy Dispatcher):**
```csharp
// Command
public class CreateOrderCommand : ICommand
{
    public Guid UserId { get; set; }
    public string ExternalOrderRef { get; set; }
}

// Handler
public class CreateOrderCommandHandler : ICommandHandler<CreateOrderCommand>
{
    public async Task HandleAsync(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        // Business logic
    }
}

// Usage in controller
await _dispatcher.DispatchAsync(command, cancellationToken);
```

**After (MediatR):**
```csharp
// Command - implements BOTH IRequest and ITransactionalCommand
public class CreateOrderCommand : IRequest, ITransactionalCommand
{
    public Guid UserId { get; set; }
    public string ExternalOrderRef { get; set; }
}

// Handler
internal class CreateOrderCommandHandler : IRequestHandler<CreateOrderCommand>
{
    public async Task Handle(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        // Business logic - automatic transaction management via MediatR pipeline
    }
}

// Usage in controller
await _mediator.Send(command, cancellationToken);
```

### Benefits of MediatR

1. **Industry standard**: De facto standard for CQRS in .NET
2. **Pipeline behaviors**: Built-in support for cross-cutting concerns (transactions, logging, validation)
3. **Single source of truth**: All commands flow through one pipeline
4. **Better testing**: Easy to mock and test behaviors in isolation
5. **Community support**: Large ecosystem of plugins and extensions

### Existing Code

Legacy `Dispatcher` and associated interfaces remain functional for backward compatibility. However:

- **New features should use MediatR** (`IRequest`/`IRequestHandler`)
- **Obsolete warnings will appear** when using legacy interfaces
- **Plan migration of existing handlers** to MediatR over time

## References

- [MediatR](https://github.com/jbogard/MediatR) - Simple mediator implementation in .NET
- [MediatR Pipeline Behaviors](https://github.com/jbogard/MediatR/wiki/Behaviors) - Cross-cutting concerns
- [EF Core Transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions)
- [ASP.NET Core ProblemDetails](https://learn.microsoft.com/en-us/aspnet/core/web-api/handle-errors)
- [OpenTelemetry .NET](https://opentelemetry.io/docs/instrumentation/net/)
