# Transactional Command Pipeline

## Overview

This document describes the transactional command pipeline implementation for write operations in ClassifiedAds, ensuring atomic database writes with deterministic rollback on failure.

## Architecture

### Core Components

1. **TransactionalAttribute** (`Application/Decorators/Transactional/TransactionalAttribute.cs`)
   - Marks command handlers that require transaction wrapping
   - Applied to handler class to automatically wrap in transaction

2. **TransactionalCommandDecorator** (`Application/Decorators/Transactional/TransactionalCommandDecorator.cs`)
   - Decorator that wraps handlers with EF Core transaction
   - Starts transaction with `ReadCommitted` isolation
   - Commits on success, rolls back on any exception
   - Emits OpenTelemetry `command.transaction` span

3. **ITransactionalCommand** (`Application/Common/Commands/ITransactionalCommand.cs`)
   - Marker interface for commands requiring transactions
   - Extends `ICommand` base interface

4. **IFailureInjector** (`Application/Common/FailureInjection/IFailureInjector.cs`)
   - Testing seam for injecting failures at specific points
   - `NoOpFailureInjector` used in production

## Usage

### Marking a Command as Transactional

```csharp
// Command implements marker interface
public class CreateOrderCommand : ITransactionalCommand
{
    public Guid UserId { get; set; }
    public string ExternalOrderRef { get; set; }
}

// Handler marked with attribute
[Transactional]
internal class CreateOrderCommandHandler : ICommandHandler<CreateOrderCommand>
{
    public async Task HandleAsync(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        // Business logic here
        // Transaction is automatically managed by decorator
        await _repository.AddAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
```

## Exception Mapping

The `GlobalExceptionHandler` maps exceptions to HTTP status codes:

| Exception Type | HTTP Status | Error Code |
|----------------|-------------|------------|
| `DbUpdateConcurrencyException` | 409 Conflict | `ConcurrencyConflict` |
| Unique constraint violation (`DbUpdateException`) | 409 Conflict | `DuplicateDetected` |
| All other unhandled exceptions | 500 Internal Server Error | (varies) |

All error responses include `correlationId` for traceability.

## Observability

### OpenTelemetry Spans

The decorator emits a `command.transaction` span with attributes:
- `command`: The command type name
- `success`: Boolean indicating successful commit
- `rolled_back`: Boolean indicating rollback occurred
- `error_code`: Error type when failure occurs

### Log Enrichment

Logs include:
- `CommandName`: The executing command type
- `CorrelationId`: Unique identifier for tracing
- Transaction outcome (committed/rolled back)

## Testing

### Failure Injection

Use `TestFailureInjector` in integration tests:

```csharp
var injector = new TestFailureInjector();
injector.ConfigureFailure("BeforeSave", new Exception("Test failure"));

// Handler will throw at CheckForFailure("BeforeSave")
```

### Test Categories

1. **Unit Tests** (`ClassifiedAds.UnitTests/Application/Decorators/`)
   - Test decorator behavior in isolation
   - Verify commit/rollback logic
   - Mock dependencies

2. **Integration Tests** (`ClassifiedAds.WebAPI.IntegrationTests/`)
   - Test full pipeline with real database
   - Verify atomicity guarantees
   - Test exception mapping

## Best Practices

1. **Handler Design**
   - Keep handlers focused on business logic
   - Avoid explicit transaction management in handlers
   - Use `IFailureInjector.CheckForFailure()` sparingly (testing only)

2. **Business Key Enforcement**
   - Define unique indexes at database level
   - Let database enforce constraints
   - Handle `DbUpdateException` for duplicates

3. **No Nested Transactions**
   - One transaction per command execution
   - Multiple `SaveChanges` calls stay in same transaction
   - Decorator manages transaction lifecycle

## Files Changed

- `Application/Common/Commands/ITransactionalCommand.cs` - Marker interface
- `Application/Decorators/Transactional/TransactionalAttribute.cs` - Attribute
- `Application/Decorators/Transactional/TransactionalCommandDecorator.cs` - Decorator
- `Application/Common/FailureInjection/IFailureInjector.cs` - Testing seam
- `Application/Common/FailureInjection/NoOpFailureInjector.cs` - Production impl
- `Application/ApplicationServicesExtensions.cs` - DI registration
- `Domain/Entities/Order.cs` - Entity with business key
- `Persistence/DbConfigurations/OrderConfiguration.cs` - Unique index
- `Application/Orders/Commands/CreateOrderCommand.cs` - Example transactional command
- `Infrastructure/Web/ExceptionHandlers/GlobalExceptionHandler.cs` - 409/500 mapping
- `WebAPI/Controllers/OrdersController.cs` - API endpoints
- Unit and integration test files
