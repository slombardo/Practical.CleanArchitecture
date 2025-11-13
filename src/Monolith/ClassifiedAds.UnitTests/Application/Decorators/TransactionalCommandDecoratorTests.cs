using ClassifiedAds.Application;
using ClassifiedAds.Application.Common.Commands;
using ClassifiedAds.Application.Decorators.Transactional;
using ClassifiedAds.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClassifiedAds.UnitTests.Application.Decorators;

/// <summary>
/// Unit tests for TransactionalCommandDecorator.
/// Uses GWT (Given/When/Then) naming and AAA (Arrange/Act/Assert) structure.
/// </summary>
public class TransactionalCommandDecoratorTests
{
    private readonly Mock<ICommandHandler<TestCommand>> _mockHandler;
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<ILogger<TransactionalCommandDecorator<TestCommand>>> _mockLogger;
    private readonly TransactionalAttribute _transactionalAttribute;
    private readonly TransactionalCommandDecorator<TestCommand> _decorator;
    private readonly Mock<IDisposable> _mockTransaction;

    public TransactionalCommandDecoratorTests()
    {
        _mockHandler = new Mock<ICommandHandler<TestCommand>>();
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockLogger = new Mock<ILogger<TransactionalCommandDecorator<TestCommand>>>();
        _transactionalAttribute = new TransactionalAttribute
        {
            IsolationLevel = IsolationLevel.ReadCommitted
        };
        _mockTransaction = new Mock<IDisposable>();

        _decorator = new TransactionalCommandDecorator<TestCommand>(
            _mockHandler.Object,
            _mockUnitOfWork.Object,
            _mockLogger.Object,
            _transactionalAttribute);
    }

    [Fact]
    public async Task Given_TransactionalCommandSucceeds_When_Handled_Then_Commits_And_EmitsSuccessSpan()
    {
        // Arrange
        var command = new TestCommand();
        _mockUnitOfWork
            .Setup(x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockTransaction.Object);

        _mockHandler
            .Setup(x => x.HandleAsync(command, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockUnitOfWork
            .Setup(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _decorator.HandleAsync(command, CancellationToken.None);

        // Assert
        _mockUnitOfWork.Verify(x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()), Times.Once);
        _mockHandler.Verify(x => x.HandleAsync(command, It.IsAny<CancellationToken>()), Times.Once);
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

        _mockHandler
            .Setup(x => x.HandleAsync(command, It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _decorator.HandleAsync(command, CancellationToken.None));

        Assert.Equal(expectedException, exception);

        // Verify transaction was started but not committed
        _mockUnitOfWork.Verify(x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()), Times.Once);
        _mockHandler.Verify(x => x.HandleAsync(command, It.IsAny<CancellationToken>()), Times.Once);
        _mockUnitOfWork.Verify(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);

        // Verify transaction was disposed (which triggers rollback)
        _mockTransaction.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Given_TransactionalCommandWithCustomIsolationLevel_When_Handled_Then_UsesCorrectIsolationLevel()
    {
        // Arrange
        var command = new TestCommand();
        var customAttribute = new TransactionalAttribute
        {
            IsolationLevel = IsolationLevel.Serializable
        };

        var decorator = new TransactionalCommandDecorator<TestCommand>(
            _mockHandler.Object,
            _mockUnitOfWork.Object,
            _mockLogger.Object,
            customAttribute);

        _mockUnitOfWork
            .Setup(x => x.BeginTransactionAsync(IsolationLevel.Serializable, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockTransaction.Object);

        _mockHandler
            .Setup(x => x.HandleAsync(command, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockUnitOfWork
            .Setup(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await decorator.HandleAsync(command, CancellationToken.None);

        // Assert
        _mockUnitOfWork.Verify(x => x.BeginTransactionAsync(IsolationLevel.Serializable, It.IsAny<CancellationToken>()), Times.Once);
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

        _mockHandler
            .Setup(x => x.HandleAsync(command, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockUnitOfWork
            .Setup(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _decorator.HandleAsync(command, CancellationToken.None));

        Assert.Equal(expectedException, exception);

        // Verify handler was called but commit failed
        _mockHandler.Verify(x => x.HandleAsync(command, It.IsAny<CancellationToken>()), Times.Once);
        _mockUnitOfWork.Verify(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Once);

        // Verify transaction was disposed
        _mockTransaction.Verify(x => x.Dispose(), Times.Once);
    }

    // Test command for unit tests
    public class TestCommand : ITransactionalCommand
    {
    }
}
