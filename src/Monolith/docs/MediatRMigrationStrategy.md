# MediatR Migration Strategy

## Executive Summary

The codebase currently uses a **hybrid approach** with 92% decorator pattern and 8% MediatR. This document outlines the strategy to complete the migration to MediatR, making it the single standard for command/query handling.

**Current State:**
- 23 command handlers using decorator pattern
- 15 query handlers using decorator pattern
- 3 MediatR requests (Orders module only)
- 8 controllers using Dispatcher, 1 using IMediator

**Target State:**
- All commands/queries use MediatR `IRequest<TResponse>`
- Cross-cutting concerns handled by `IPipelineBehavior<,>`
- Dispatcher infrastructure deprecated and removed

## Why Migrate?

### Problems with Current Hybrid Approach

1. **Duplicate implementations** - CreateOrderCommand vs CreateOrderRequest
2. **Inconsistent patterns** - Developers confused about which to use
3. **Maintenance burden** - Two infrastructures to maintain
4. **Limited decorator adoption** - Only 2 of 26 handlers use decorators
5. **Missing observability** - Decorator pattern lacks OpenTelemetry integration

### Benefits of Full MediatR Adoption

1. **Industry standard** - Well-known pattern, easier onboarding
2. **Better observability** - OpenTelemetry spans with error code mapping
3. **Composable behaviors** - Stack multiple concerns (transactions, logging, validation)
4. **Request/Response model** - Commands return results naturally
5. **Simplified testing** - No decorator wrapping needed

## Migration Phases

### Phase 0: Immediate Actions (Week 1)
**Goal: Stop the bleeding**

1. **Remove duplicate Order handlers**
   - Delete `CreateOrderCommand.cs` (decorator version)
   - Delete `GetOrderQuery.cs` (decorator version)
   - Keep only MediatR versions

2. **Add build-time enforcement** (see Roslyn Analyzer below)

3. **Communicate to team**
   - Present this strategy in team meeting
   - Add ADR to document the decision
   - Update CONTRIBUTING.md with new pattern requirements

### Phase 1: Port Decorators to Behaviors (Week 2)
**Goal: Ensure feature parity**

Port existing decorators to MediatR behaviors:

| Decorator | MediatR Behavior | Status |
|-----------|-----------------|--------|
| TransactionalCommandDecorator | TransactionalBehavior | ✅ Done |
| AuditLogCommandDecorator | AuditLogBehavior | TODO |
| DatabaseRetryCommandDecorator | DatabaseRetryBehavior | TODO |

```csharp
// Example: AuditLogBehavior
public class AuditLogBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (request is not IAuditedRequest)
            return await next();

        var requestJson = JsonSerializer.Serialize(request);
        _logger.LogInformation("Executing {Request}: {Json}", typeof(TRequest).Name, requestJson);

        var response = await next();

        var responseJson = JsonSerializer.Serialize(response);
        _logger.LogInformation("Completed {Request}: {Json}", typeof(TRequest).Name, responseJson);

        return response;
    }
}
```

### Phase 2: Migrate Low-Risk Modules (Weeks 3-4)
**Goal: Build confidence**

Start with modules that have minimal business logic:

1. **EmailMessages** (2 commands)
   - SendEmailMessagesCommand → SendEmailMessagesRequest

2. **SmsMessages** (2 commands)
   - SendSmsMessagesCommand → SendSmsMessagesRequest

3. **ConfigurationEntries** (2 queries)
   - GetConfigurationEntriesQuery → GetConfigurationEntriesRequest

**Migration Pattern per Handler:**
```csharp
// OLD: Decorator pattern
public class SendEmailMessagesCommand : ICommand { }
public class SendEmailMessagesCommandHandler : ICommandHandler<SendEmailMessagesCommand> { }

// NEW: MediatR pattern
public class SendEmailMessagesRequest : IRequest<SendEmailMessagesResponse> { }
internal class SendEmailMessagesRequestHandler : IRequestHandler<SendEmailMessagesRequest, SendEmailMessagesResponse> { }
```

### Phase 3: Migrate Core Modules (Weeks 5-8)
**Goal: Complete business logic migration**

1. **Products** (6 handlers)
2. **Users** (10 handlers)
3. **Roles** (8 handlers)
4. **Files** (4 handlers)

### Phase 4: Migrate Generic Handlers (Week 9)
**Goal: Handle aggregate root operations**

The generic handlers need special attention:

```csharp
// Current generic pattern
public class AddOrUpdateEntityCommandHandler<TEntity> : ICommandHandler<AddOrUpdateEntityCommand<TEntity>>

// New MediatR pattern
public class AddOrUpdateEntityRequest<TEntity> : IRequest<AddOrUpdateEntityResponse<TEntity>>
    where TEntity : Entity<Guid>, IAggregateRoot

public class AddOrUpdateEntityRequestHandler<TEntity> : IRequestHandler<AddOrUpdateEntityRequest<TEntity>, AddOrUpdateEntityResponse<TEntity>>
```

### Phase 5: Handle Domain Events (Week 10)
**Goal: Migrate event publishing**

**Decision Point:** Use MediatR notifications or keep separate event system?

**Option A: MediatR Notifications (Recommended)**
```csharp
public class ProductCreatedEvent : INotification
{
    public Product Product { get; set; }
}

public class ProductCreatedEventHandler : INotificationHandler<ProductCreatedEvent>
{
    public async Task Handle(ProductCreatedEvent notification, CancellationToken ct)
    {
        // Handle event
    }
}
```

**Option B: Keep Separate Event System**
- Less migration work
- But maintains two systems

### Phase 6: Update Controllers and Services (Week 11)
**Goal: Remove all Dispatcher usage**

Update all 8 controllers + 7 services:
- Replace `Dispatcher` with `IMediator`
- Update all `.DispatchAsync()` to `_mediator.Send()`

### Phase 7: Cleanup (Week 12)
**Goal: Remove old infrastructure**

1. Delete decorator infrastructure:
   - `Dispatcher.cs`
   - `HandlerFactory.cs`
   - `ICommand.cs`, `IQuery.cs`
   - `ICommandHandler.cs`, `IQueryHandler.cs`
   - All decorator classes
   - `Mappings.cs`, `MappingAttribute.cs`

2. Remove from DI registration:
   - `AddMessageHandlers()` extension method

3. Update tests to use MediatR only

## Why We Still Have Decorator Pattern Code

You'll notice that the codebase still contains many files using `ICommand`, `IQuery`, `ICommandHandler<>`, and `IQueryHandler<>`. This is intentional and temporary:

### Legacy Code Rationale

1. **Gradual Migration**: We're migrating 38+ handlers over 12 weeks. This can't happen overnight.

2. **No Big Bang Rewrites**: A complete rewrite would introduce risk and block feature development. Incremental migration is safer.

3. **Maintaining Functionality**: Existing handlers must continue working while new code uses MediatR.

4. **Team Transition**: Gives developers time to learn the new pattern while maintaining productivity.

### How to Identify Legacy vs. New Code

**Legacy Code (TO BE MIGRATED):**
- Uses `ICommand` or `IQuery<TResult>`
- Implements `ICommandHandler<>` or `IQueryHandler<>`
- Uses `Dispatcher.DispatchAsync()`
- Has decorator attributes like `[Transactional]`
- **Generates CS0618 "obsolete" compiler warnings**

**New Code (CURRENT STANDARD):**
- Uses `IRequest<TResponse>`
- Implements `IRequestHandler<TRequest, TResponse>`
- Uses `IMediator.Send()`
- Uses marker interfaces like `ITransactionalRequest`
- **No compiler warnings**

### What to Do When You Encounter Legacy Code

1. **Do NOT copy the pattern** for new features
2. **Do NOT extend** legacy handlers
3. **Do consider migrating** the handler if you're modifying it significantly
4. **Do follow** the MediatR pattern for all new code
5. **Do check** the migration timeline to see when that module is scheduled for migration

### Temporary Suppression (Not Recommended)

If you absolutely must suppress the obsolete warnings for existing code:

```csharp
#pragma warning disable CS0618 // Type or member is obsolete
// Legacy code here
#pragma warning restore CS0618
```

**Only do this for maintaining existing handlers, not for new code.**

## High-Visibility Enforcement

### 1. Obsolete Attributes (Compile-Time Warnings)

All decorator pattern interfaces are marked with `[Obsolete]` attributes:

```csharp
[Obsolete("ICommand is deprecated. Use MediatR IRequest<TResponse> instead. See docs/MediatRMigrationStrategy.md")]
public interface ICommand { }

[Obsolete("ICommandHandler<T> is deprecated. Use MediatR IRequestHandler<TRequest, TResponse> instead.")]
public interface ICommandHandler<TCommand> where TCommand : ICommand { }
```

This provides:
- **Immediate feedback** - Warnings appear in IDE as you type
- **Clear guidance** - Links to documentation
- **Non-breaking** - Existing code still compiles
- **Gradual enforcement** - Can escalate to errors via `#pragma warning error CS0618`

### 2. EditorConfig Rules (Optional Escalation)

To treat obsolete usage as errors (after migration is further along):

```ini
# .editorconfig
# Escalate obsolete warnings to errors
dotnet_diagnostic.CS0618.severity = error
```

**Note:** Only enable this after most handlers are migrated, or use it selectively per project.

### 3. Architecture Tests

Add ArchUnitNET tests that enforce the pattern:

```csharp
[Fact]
public void New_Handlers_Should_Use_MediatR_Pattern()
{
    var result = Types.InAssembly(typeof(ApplicationServicesExtensions).Assembly)
        .That()
        .ImplementInterface(typeof(IRequestHandler<,>))
        .Or()
        .ImplementInterface(typeof(ICommandHandler<>))
        .Should()
        .ImplementInterface(typeof(IRequestHandler<,>))
        .GetResult();

    result.IsSuccessful.Should().BeTrue(result.FailingTypeNames);
}
```

### 4. PR Template Checklist

Add to `.github/PULL_REQUEST_TEMPLATE.md`:

```markdown
## Architecture Compliance

- [ ] No new `ICommand` or `IQuery` implementations (use `IRequest<T>` instead)
- [ ] No new `ICommandHandler<>` or `IQueryHandler<>` (use `IRequestHandler<,>` instead)
- [ ] Followed MediatR pattern as documented in `docs/TransactionalPipeline.md`
- [ ] Added appropriate marker interfaces (`ITransactionalRequest`, `IAuditedRequest`, etc.)
```

### 5. README Banner

Add prominent warning to README.md:

```markdown
## ⚠️ Architecture Migration In Progress

This codebase is migrating from decorator pattern to MediatR.

**DO NOT** create new handlers using:
- `ICommand`, `IQuery`
- `ICommandHandler<>`, `IQueryHandler<>`

**DO** use MediatR pattern:
- `IRequest<TResponse>`
- `IRequestHandler<TRequest, TResponse>`

See [Migration Strategy](docs/MediatRMigrationStrategy.md) for details.
```

### 6. Git Pre-Commit Hook

Add a pre-commit hook that warns about decorator usage:

```bash
#!/bin/bash
# .git/hooks/pre-commit

if git diff --cached --name-only | xargs grep -l "ICommandHandler\|IQueryHandler" 2>/dev/null; then
    echo "⚠️  WARNING: You're adding code that uses the deprecated decorator pattern."
    echo "Please use MediatR IRequest<T> pattern instead."
    echo "See docs/MediatRMigrationStrategy.md"
    read -p "Continue anyway? (y/N) " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        exit 1
    fi
fi
```

## Team Communication Plan

### Week 1: Announcement
- [ ] Team meeting presentation (15 min)
- [ ] Slack/Teams announcement with link to docs
- [ ] ADR published (see below)

### Ongoing: Visibility
- [ ] Add migration status to sprint dashboard
- [ ] Weekly progress updates in standup
- [ ] Celebrate completed module migrations

### Documentation
- [ ] This migration strategy document
- [ ] ADR for architectural decision
- [ ] Updated CONTRIBUTING.md
- [ ] TransactionalPipeline.md (already created)

## Measuring Progress

Track migration progress with this query:

```bash
# Count decorator handlers
echo "Decorator handlers: $(grep -r "ICommandHandler\|IQueryHandler" --include="*.cs" ClassifiedAds.Application | grep "class.*:" | wc -l)"

# Count MediatR handlers
echo "MediatR handlers: $(grep -r "IRequestHandler<" --include="*.cs" ClassifiedAds.Application | grep "class.*:" | wc -l)"
```

**Target:** 0 decorator handlers, ~40 MediatR handlers

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Breaking changes | Comprehensive test coverage before each migration |
| Team resistance | Clear documentation, team buy-in, demonstrate benefits |
| Incomplete migration | Build enforcement prevents new decorator usage |
| Performance regression | Benchmark critical paths before/after |
| Lost functionality | Ensure all decorator features ported to behaviors |

## Success Criteria

1. **Zero new decorator handlers** after Phase 0
2. **All modules migrated** by Week 12
3. **No Dispatcher usage** in codebase
4. **All tests pass** with MediatR
5. **Team adoption** - everyone using new pattern

## Next Steps

1. **Immediate**: Review and approve this strategy
2. **This week**: Present to team, get buy-in
3. **Next week**: Start Phase 0 (stop the bleeding)
4. **Ongoing**: Execute phases 1-7 over 12 weeks
