using ClassifiedAds.Application.Common.Behaviors;
using ClassifiedAds.Domain.Repositories;
using MediatR;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClassifiedAds.UnitTests.Application.Behaviors;

[Collection("TransactionalBehavior")]
public class TransactionalBehaviorTests : IDisposable
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<IDisposable> _mockTransaction;
    private readonly ActivityListener _activityListener;
    private readonly List<Activity> _capturedActivities;

    public TransactionalBehaviorTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockTransaction = new Mock<IDisposable>();

        _mockUnitOfWork
            .Setup(x => x.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockTransaction.Object);

        // Set up activity listener to capture spans
        _capturedActivities = new List<Activity>();
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "ClassifiedAds.Application.Transactional",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => _capturedActivities.Add(activity),
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    public void Dispose()
    {
        _activityListener.Dispose();
    }

    [Fact]
    public async Task Given_TransactionalCommandSucceeds_When_Handled_Then_Commits_And_EmitsSuccessSpan()
    {
        // Arrange
        _capturedActivities.Clear();
        var request = new TestTransactionalRequest { Data = "test" };
        var expectedResponse = new TestResponse { Result = "success" };
        RequestHandlerDelegate<TestResponse> next = () => Task.FromResult(expectedResponse);

        var behavior = new TransactionalBehavior<TestTransactionalRequest, TestResponse>(
            _mockUnitOfWork.Object,
            new Mock<ILogger<TransactionalBehavior<TestTransactionalRequest, TestResponse>>>().Object);

        // Act
        var response = await behavior.Handle(request, next, CancellationToken.None);

        // Assert - response returned correctly
        Assert.Same(expectedResponse, response);

        // Assert - transaction lifecycle
        _mockUnitOfWork.Verify(
            x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockUnitOfWork.Verify(
            x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()),
            Times.Once);
        _mockTransaction.Verify(
            x => x.Dispose(),
            Times.Once);

        // Assert - OpenTelemetry span emitted with correct attributes
        var activity = _capturedActivities.SingleOrDefault(a => a.OperationName == "command.transaction");
        Assert.NotNull(activity);
        Assert.Equal("TestTransactionalRequest", activity.GetTagItem("command"));
        Assert.Equal(true, activity.GetTagItem("success"));
        Assert.Equal(false, activity.GetTagItem("rolled_back"));
        Assert.Null(activity.GetTagItem("error_code"));
    }

    [Fact]
    public async Task Given_TransactionalCommandThrows_When_Handled_Then_RollsBack_And_EmitsRolledBackSpan()
    {
        // Arrange
        _capturedActivities.Clear();
        var request = new TestTransactionalRequest { Data = "test" };
        var expectedException = new InvalidOperationException("Test exception");
        RequestHandlerDelegate<TestResponse> next = () => throw expectedException;

        var behavior = new TransactionalBehavior<TestTransactionalRequest, TestResponse>(
            _mockUnitOfWork.Object,
            new Mock<ILogger<TransactionalBehavior<TestTransactionalRequest, TestResponse>>>().Object);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => behavior.Handle(request, next, CancellationToken.None));

        // Assert - exception propagated
        Assert.Same(expectedException, exception);

        // Assert - transaction lifecycle (no commit, but disposed)
        _mockUnitOfWork.Verify(
            x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockUnitOfWork.Verify(
            x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        _mockTransaction.Verify(
            x => x.Dispose(),
            Times.Once);

        // Assert - OpenTelemetry span emitted with rolled back attributes
        var activity = _capturedActivities.SingleOrDefault(a => a.OperationName == "command.transaction");
        Assert.NotNull(activity);
        Assert.Equal("TestTransactionalRequest", activity.GetTagItem("command"));
        Assert.Equal(false, activity.GetTagItem("success"));
        Assert.Equal(true, activity.GetTagItem("rolled_back"));
        Assert.Equal("InvalidOperationException", activity.GetTagItem("error_code"));
    }

    [Fact]
    public async Task Given_NonTransactionalRequest_When_Handled_Then_SkipsTransactionWrapping_And_NoSpanEmitted()
    {
        // Arrange
        _capturedActivities.Clear();
        var request = new TestRequest { Data = "test" };
        var expectedResponse = new TestResponse { Result = "success" };
        RequestHandlerDelegate<TestResponse> next = () => Task.FromResult(expectedResponse);

        var behavior = new TransactionalBehavior<TestRequest, TestResponse>(
            _mockUnitOfWork.Object,
            new Mock<ILogger<TransactionalBehavior<TestRequest, TestResponse>>>().Object);

        // Act
        var response = await behavior.Handle(request, next, CancellationToken.None);

        // Assert - response returned
        Assert.Same(expectedResponse, response);

        // Assert - no transaction started
        _mockUnitOfWork.Verify(
            x => x.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockUnitOfWork.Verify(
            x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert - no OpenTelemetry span emitted
        var activity = _capturedActivities.SingleOrDefault(a => a.OperationName == "command.transaction");
        Assert.Null(activity);
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

        // Assert - handler called once within single transaction
        Assert.Equal(1, callCount);
        _mockUnitOfWork.Verify(
            x => x.BeginTransactionAsync(IsolationLevel.ReadCommitted, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockUnitOfWork.Verify(
            x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Given_DbUpdateConcurrencyException_When_Handled_Then_EmitsSpanWithCorrectErrorCode()
    {
        // Arrange
        _capturedActivities.Clear();
        var request = new TestTransactionalRequest { Data = "test" };
        var expectedException = new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("Concurrency conflict");
        RequestHandlerDelegate<TestResponse> next = () => throw expectedException;

        var behavior = new TransactionalBehavior<TestTransactionalRequest, TestResponse>(
            _mockUnitOfWork.Object,
            new Mock<ILogger<TransactionalBehavior<TestTransactionalRequest, TestResponse>>>().Object);

        // Act
        var exception = await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException>(
            () => behavior.Handle(request, next, CancellationToken.None));

        // Assert - span has correct error code
        var activity = _capturedActivities.SingleOrDefault(a => a.OperationName == "command.transaction");
        Assert.NotNull(activity);
        Assert.Equal(false, activity.GetTagItem("success"));
        Assert.Equal(true, activity.GetTagItem("rolled_back"));
        Assert.Equal("DbUpdateConcurrencyException", activity.GetTagItem("error_code"));
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
