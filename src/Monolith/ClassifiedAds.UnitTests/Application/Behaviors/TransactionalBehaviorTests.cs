using ClassifiedAds.Application.Common.Behaviors;
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

public class TransactionalBehaviorTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<ILogger<TransactionalBehavior<TestRequest, TestResponse>>> _mockLogger;
    private readonly Mock<IDisposable> _mockTransaction;
    private readonly TransactionalBehavior<TestRequest, TestResponse> _behavior;

    public TransactionalBehaviorTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockLogger = new Mock<ILogger<TransactionalBehavior<TestRequest, TestResponse>>>();
        _mockTransaction = new Mock<IDisposable>();

        _mockUnitOfWork
            .Setup(x => x.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockTransaction.Object);

        _behavior = new TransactionalBehavior<TestRequest, TestResponse>(
            _mockUnitOfWork.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task Given_TransactionalCommandSucceeds_When_Handled_Then_Commits_And_EmitsSuccessSpan()
    {
        // Arrange
        var request = new TestTransactionalRequest { Data = "test" };
        var expectedResponse = new TestResponse { Result = "success" };
        RequestHandlerDelegate<TestResponse> next = () => Task.FromResult(expectedResponse);

        var behavior = new TransactionalBehavior<TestTransactionalRequest, TestResponse>(
            _mockUnitOfWork.Object,
            new Mock<ILogger<TransactionalBehavior<TestTransactionalRequest, TestResponse>>>().Object);

        // Act
        var response = await behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.Same(expectedResponse, response);
        _mockUnitOfWork.Verify(
            x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()),
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
        var request = new TestTransactionalRequest { Data = "test" };
        var expectedException = new InvalidOperationException("Test exception");
        RequestHandlerDelegate<TestResponse> next = () => throw expectedException;

        var behavior = new TransactionalBehavior<TestTransactionalRequest, TestResponse>(
            _mockUnitOfWork.Object,
            new Mock<ILogger<TransactionalBehavior<TestTransactionalRequest, TestResponse>>>().Object);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => behavior.Handle(request, next, CancellationToken.None));

        // Assert
        Assert.Same(expectedException, exception);
        _mockUnitOfWork.Verify(
            x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockUnitOfWork.Verify(
            x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        _mockTransaction.Verify(
            x => x.Dispose(),
            Times.Once);
    }

    [Fact]
    public async Task Given_NonTransactionalRequest_When_Handled_Then_SkipsTransactionWrapping()
    {
        // Arrange
        var request = new TestRequest { Data = "test" };
        var expectedResponse = new TestResponse { Result = "success" };
        RequestHandlerDelegate<TestResponse> next = () => Task.FromResult(expectedResponse);

        // Act
        var response = await _behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.Same(expectedResponse, response);
        _mockUnitOfWork.Verify(
            x => x.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockUnitOfWork.Verify(
            x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Given_TransactionalCommandWithMultipleSaveChanges_When_Handled_Then_AllInSameTransaction()
    {
        // Arrange
        var request = new TestTransactionalRequest { Data = "test" };
        var expectedResponse = new TestResponse { Result = "success" };
        var callCount = 0;

        RequestHandlerDelegate<TestResponse> next = () =>
        {
            callCount++;
            return Task.FromResult(expectedResponse);
        };

        var behavior = new TransactionalBehavior<TestTransactionalRequest, TestResponse>(
            _mockUnitOfWork.Object,
            new Mock<ILogger<TransactionalBehavior<TestTransactionalRequest, TestResponse>>>().Object);

        // Act
        await behavior.Handle(request, next, CancellationToken.None);

        // Assert
        Assert.Equal(1, callCount);
        _mockUnitOfWork.Verify(
            x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockUnitOfWork.Verify(
            x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    public class TestRequest : IRequest<TestResponse>
    {
        public string Data { get; set; }
    }

    public class TestTransactionalRequest : IRequest<TestResponse>, ITransactionalRequest
    {
        public string Data { get; set; }
    }

    public class TestResponse
    {
        public string Result { get; set; }
    }
}
