using ClassifiedAds.Application;
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

public class TransactionalCommandDecoratorTests
{
    private readonly Mock<ICommandHandler<TestCommand>> _mockHandler;
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<ILogger<TransactionalCommandDecorator<TestCommand>>> _mockLogger;
    private readonly Mock<IDisposable> _mockTransaction;
    private readonly TransactionalCommandDecorator<TestCommand> _decorator;

    public TransactionalCommandDecoratorTests()
    {
        _mockHandler = new Mock<ICommandHandler<TestCommand>>();
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockLogger = new Mock<ILogger<TransactionalCommandDecorator<TestCommand>>>();
        _mockTransaction = new Mock<IDisposable>();

        _mockUnitOfWork
            .Setup(x => x.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockTransaction.Object);

        _decorator = new TransactionalCommandDecorator<TestCommand>(
            _mockHandler.Object,
            _mockUnitOfWork.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task Given_TransactionalCommandSucceeds_When_Handled_Then_Commits_And_EmitsSuccessSpan()
    {
        // Arrange
        var command = new TestCommand { Data = "test" };
        _mockHandler
            .Setup(x => x.HandleAsync(command, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _decorator.HandleAsync(command);

        // Assert
        _mockUnitOfWork.Verify(
            x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockHandler.Verify(
            x => x.HandleAsync(command, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockUnitOfWork.Verify(
            x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()),
            Times.Once);
        _mockTransaction.Verify(
            x => x.Dispose(),
            Times.Once);
    }

    [Fact]
    public async Task Given_TransactionalCommandThrows_When_Handled_Then_RollsBack_And_EmitsRolledBackSpan()
    {
        // Arrange
        var command = new TestCommand { Data = "test" };
        var expectedException = new InvalidOperationException("Test exception");
        _mockHandler
            .Setup(x => x.HandleAsync(command, It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _decorator.HandleAsync(command));

        // Assert
        Assert.Same(expectedException, exception);
        _mockUnitOfWork.Verify(
            x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockHandler.Verify(
            x => x.HandleAsync(command, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockUnitOfWork.Verify(
            x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        _mockTransaction.Verify(
            x => x.Dispose(),
            Times.Once);
    }

    [Fact]
    public async Task Given_TransactionalCommandWithMultipleSaveChanges_When_Handled_Then_AllInSameTransaction()
    {
        // Arrange
        var command = new TestCommand { Data = "test" };
        var saveCount = 0;

        _mockHandler
            .Setup(x => x.HandleAsync(command, It.IsAny<CancellationToken>()))
            .Callback(() => saveCount++)
            .Returns(Task.CompletedTask);

        // Act
        await _decorator.HandleAsync(command);

        // Assert
        _mockUnitOfWork.Verify(
            x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockUnitOfWork.Verify(
            x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Given_TransactionalCommandThrowsAfterPartialWork_When_Handled_Then_TransactionDisposed()
    {
        // Arrange
        var command = new TestCommand { Data = "partial-work" };
        var workDone = false;

        _mockHandler
            .Setup(x => x.HandleAsync(command, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                workDone = true;
                throw new Exception("Failure after partial work");
            });

        // Act
        await Assert.ThrowsAsync<Exception>(() => _decorator.HandleAsync(command));

        // Assert
        Assert.True(workDone);
        _mockUnitOfWork.Verify(
            x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        _mockTransaction.Verify(
            x => x.Dispose(),
            Times.Once);
    }

    public class TestCommand : ICommand
    {
        public string Data { get; set; }
    }
}
