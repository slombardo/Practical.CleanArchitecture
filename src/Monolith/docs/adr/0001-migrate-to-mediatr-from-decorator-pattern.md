# ADR 0001: Migrate to MediatR from Decorator Pattern

## Status

**Accepted** - 2025-11-13

## Context

The ClassifiedAds.Monolith codebase currently uses a custom decorator pattern for handling commands and queries. The implementation includes:

- Custom `Dispatcher` class for routing commands/queries
- `ICommand`/`IQuery` interfaces for request types
- `ICommandHandler<>`/`IQueryHandler<>` for handlers
- Decorator attributes (`[AuditLog]`, `[DatabaseRetry]`, `[Transactional]`) for cross-cutting concerns
- `HandlerFactory` for building decorator chains via reflection

**Current Usage:**
- 23 command handlers using decorator pattern
- 15 query handlers using decorator pattern
- 8 controllers using `Dispatcher`
- 7 background services using `Dispatcher`

**Problems Identified:**
1. Low decorator utilization - only 2 of 26 handlers use decorators
2. Custom infrastructure requiring maintenance
3. No built-in support for request/response model
4. Limited observability and tracing support
5. Inconsistent with industry standards
6. Complex decorator chain resolution via reflection

Recently, MediatR was introduced for the Orders module to implement transactional behavior with OpenTelemetry support. This created a **hybrid state** with two competing patterns:
- 3 MediatR-based handlers (Orders module)
- 38 decorator-based handlers (everything else)

## Decision

**We will migrate entirely from the decorator pattern to MediatR**, making MediatR the single standard for all command and query handling in the application.

### Key Changes

1. **Replace decorator pattern with MediatR pipeline behaviors**
   - `[Transactional]` decorator → `TransactionalBehavior<TRequest, TResponse>`
   - `[AuditLog]` decorator → `AuditLogBehavior<TRequest, TResponse>`
   - `[DatabaseRetry]` decorator → `DatabaseRetryBehavior<TRequest, TResponse>`

2. **Use marker interfaces for opt-in behaviors**
   - `ITransactionalRequest` - wrap in database transaction
   - `IAuditedRequest` - log request/response
   - `IRetryableRequest` - retry on transient failures

3. **Standardize on request/response model**
   - All commands/queries implement `IRequest<TResponse>`
   - All handlers implement `IRequestHandler<TRequest, TResponse>`

4. **Enforce via build-time checks**
   - Roslyn analyzer prevents new decorator handlers
   - Architecture tests validate patterns

## Consequences

### Positive

1. **Industry standard pattern** - MediatR is widely used and understood
2. **Better observability** - OpenTelemetry integration with error code mapping
3. **Simplified testing** - No decorator chain setup required
4. **Request/response model** - Natural return values from commands
5. **Composable behaviors** - Easy to stack multiple pipeline behaviors
6. **Reduced maintenance** - Less custom infrastructure to maintain
7. **Clear migration path** - Phased approach over 12 weeks

### Negative

1. **Migration effort** - ~40 handlers to migrate over 12 weeks
2. **Learning curve** - Team needs to learn MediatR patterns
3. **Temporary hybrid state** - During migration, both patterns coexist
4. **Domain events decision** - Need to decide on MediatR notifications vs. keeping separate event system

### Neutral

1. **Package dependency** - Adding MediatR NuGet package (widely used, MIT license)
2. **Performance** - MediatR uses optimized reflection, performance should be similar or better

## Compliance

### How to Create New Handlers (DO THIS)

```csharp
// 1. Define request and response
public class CreateProductRequest : IRequest<CreateProductResponse>, ITransactionalRequest
{
    public string Name { get; set; }
    public decimal Price { get; set; }
}

public class CreateProductResponse
{
    public Guid ProductId { get; set; }
}

// 2. Implement handler
internal class CreateProductRequestHandler : IRequestHandler<CreateProductRequest, CreateProductResponse>
{
    public async Task<CreateProductResponse> Handle(CreateProductRequest request, CancellationToken ct)
    {
        // Implementation
    }
}

// 3. Use in controller
public class ProductsController : ControllerBase
{
    private readonly IMediator _mediator;

    [HttpPost]
    public async Task<ActionResult> Create(CreateProductModel model)
    {
        var response = await _mediator.Send(new CreateProductRequest { ... });
        return Created(...);
    }
}
```

### What NOT to Do (DEPRECATED)

```csharp
// ❌ DON'T: Create new ICommand implementations
public class CreateProductCommand : ICommand { }

// ❌ DON'T: Create new ICommandHandler implementations
public class CreateProductCommandHandler : ICommandHandler<CreateProductCommand> { }

// ❌ DON'T: Use Dispatcher in new code
await _dispatcher.DispatchAsync(new CreateProductCommand());

// ❌ DON'T: Add decorator attributes
[Transactional]
[AuditLog]
public class MyCommandHandler : ICommandHandler<MyCommand> { }
```

## Migration Timeline

- **Week 1**: Phase 0 - Stop new decorator usage, remove duplicates
- **Weeks 2**: Phase 1 - Port all decorators to behaviors
- **Weeks 3-4**: Phase 2 - Migrate EmailMessages, SmsMessages
- **Weeks 5-8**: Phase 3 - Migrate Products, Users, Roles, Files
- **Week 9**: Phase 4 - Migrate generic handlers
- **Week 10**: Phase 5 - Handle domain events
- **Week 11**: Phase 6 - Update all controllers/services
- **Week 12**: Phase 7 - Remove old infrastructure

## Related Documents

- [MediatR Migration Strategy](../MediatRMigrationStrategy.md) - Detailed migration plan
- [Transactional Pipeline](../TransactionalPipeline.md) - How the new pattern works
- [MediatR Documentation](https://github.com/jbogard/MediatR/wiki)

## Decision Makers

- [Your Name] - Lead Developer
- [Team Members] - Development Team

## Date

2025-11-13
