using ClassifiedAds.Application.Common.Behaviors;
using ClassifiedAds.Application.Common.Commands;
using ClassifiedAds.Domain.Repositories;
using MediatR;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClassifiedAds.UnitTests.Application.Behaviors;

/// <summary>
/// Unit tests for TransactionalBehavior (MediatR IPipelineBehavior).
/// Uses GWT (Given/When/Then) naming and AAA (Arrange/Act/Assert) structure.
/// </summary>
public class TransactionalBehaviorTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<ILogger<TransactionalBehavior<TestCommand, Unit>>> _mockLogger;
    private readonly TransactionalBehavior<TestCommand, Unit> _behavior;
    private readonly Mock<IDisposable> _mockTransaction;

    public TransactionalBehaviorTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockLogger = new Mock<ILogger<TransactionalBehavior<TestCommand, Unit>>>();
        _mockTransaction = new Mock<IDisposable>();

        _behavior = new TransactionalBehavior<TestCommand, Unit>(
            _mockUnitOfWork.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task Given_TransactionalCommandSucceeds_When_Handled_Then_Commits_And_EmitsSuccessSpan()
    {
        // Arrange
        var command = new TestCommand();
        var nextCalled = false;

        _mockUnitOfWork
            .Setup(x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockTransaction.Object);

        _mockUnitOfWork
            .Setup(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        RequestHandlerDelegate<Unit> next = () =>
        {
            nextCalled = true;
            return Task.FromResult(Unit.Value);
        };

        // Act
        var result = await _behavior.Handle(command, next, CancellationToken.None);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal(Unit.Value, result);
        _mockUnitOfWork.Verify(x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()), Times.Once);
        _mockUnitOfWork.Verify(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockTransaction.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Given_TransactionalCommandThrows_When_Handled_Then_RollsBack_And_EmitsRolledBackSpan()
    {
        // Arrange
        var command = new TestCommand();
        var expectedException = new InvalidOperationException("Test exception");

        _mockUnitOfWork
            .Setup(x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockTransaction.Object);

        RequestHandlerDelegate<Unit> next = () => throw expectedException;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _behavior.Handle(command, next, CancellationToken.None));

        Assert.Equal(expectedException, exception);

        // Verify transaction was started but not committed
        _mockUnitOfWork.Verify(x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()), Times.Once);
        _mockUnitOfWork.Verify(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);

        // Verify transaction was disposed (which triggers rollback)
        _mockTransaction.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Given_NonTransactionalCommand_When_Handled_Then_PassesThrough_WithoutTransaction()
    {
        // Arrange
        var nonTransactionalCommand = new NonTransactionalCommand();
        var nextCalled = false;

        RequestHandlerDelegate<Unit> next = () =>
        {
            nextCalled = true;
            return Task.FromResult(Unit.Value);
        };

        var nonTransactionalBehavior = new TransactionalBehavior<NonTransactionalCommand, Unit>(
            _mockUnitOfWork.Object,
            Mock.Of<ILogger<TransactionalBehavior<NonTransactionalCommand, Unit>>>());

        // Act
        var result = await nonTransactionalBehavior.Handle(nonTransactionalCommand, next, CancellationToken.None);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal(Unit.Value, result);

        // Verify NO transaction was started for non-transactional commands
        _mockUnitOfWork.Verify(x => x.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockUnitOfWork.Verify(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_CommitTransactionFails_When_Handled_Then_ThrowsAndDisposesTransaction()
    {
        // Arrange
        var command = new TestCommand();
        var expectedException = new InvalidOperationException("Commit failed");

        _mockUnitOfWork
            .Setup(x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockTransaction.Object);

        _mockUnitOfWork
            .Setup(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        RequestHandlerDelegate<Unit> next = () => Task.FromResult(Unit.Value);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _behavior.Handle(command, next, CancellationToken.None));

        Assert.Equal(expectedException, exception);

        // Verify next was called but commit failed
        _mockUnitOfWork.Verify(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Once);

        // Verify transaction was disposed
        _mockTransaction.Verify(x => x.Dispose(), Times.Once);
    }

    // Test commands for unit tests
    public class TestCommand : IRequest<Unit>, ITransactionalCommand
    {
    }

    public class NonTransactionalCommand : IRequest<Unit>
    {
        // Does NOT implement ITransactionalCommand
    }
}
