using ClassifiedAds.Application;
using ClassifiedAds.Application.Common.Testing;
using ClassifiedAds.Application.Decorators.Transactional;
using ClassifiedAds.Application.Orders.Commands;
using ClassifiedAds.Domain.Entities;
using ClassifiedAds.Domain.Repositories;
using ClassifiedAds.UnitTests.Application.Testing;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClassifiedAds.UnitTests.Application.Orders;

/// <summary>
/// Integration tests for CreateOrderCommand with transactional guarantees.
/// Tests failure injection scenarios and validates rollback behavior.
/// Uses GWT (Given/When/Then) naming and AAA (Arrange/Act/Assert) structure.
/// </summary>
public class CreateOrderCommandTransactionalTests
{
    [Fact]
    public async Task Given_UnexpectedFailure_BeforeSave_When_CreatingOrder_Then_ThrowsException_And_NoPersistedRows()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var externalOrderRef = "TEST-ORDER-001";
        var command = new CreateOrderCommand
        {
            UserId = userId,
            ExternalOrderRef = externalOrderRef,
            TotalAmount = 100.00m,
            Currency = "USD",
            Notes = "Test order"
        };

        var failureInjector = new ConfigurableFailureInjector();
        failureInjector.ConfigureFailure("BeforeSave", new InvalidOperationException("Simulated failure before save"));

        var mockRepository = new Mock<IRepository<Order, Guid>>();
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        var mockTransaction = new Mock<IDisposable>();

        // Track if AddOrUpdate was called
        Order addedOrder = null;
        mockRepository
            .Setup(x => x.AddOrUpdateAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Callback<Order, CancellationToken>((order, ct) => addedOrder = order)
            .Returns(Task.CompletedTask);

        mockUnitOfWork
            .Setup(x => x.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockTransaction.Object);

        // SaveChanges should not be called due to failure before save
        mockUnitOfWork
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(0));

        var handler = new CreateOrderCommandHandler(
            mockRepository.Object,
            mockUnitOfWork.Object,
            failureInjector);

        var logger = Mock.Of<ILogger<TransactionalCommandDecorator<CreateOrderCommand>>>();
        var attribute = new TransactionalAttribute { IsolationLevel = IsolationLevel.ReadCommitted };
        var decorator = new TransactionalCommandDecorator<CreateOrderCommand>(
            handler,
            mockUnitOfWork.Object,
            logger,
            attribute);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await decorator.HandleAsync(command, CancellationToken.None));

        Assert.Equal("Simulated failure before save", exception.Message);

        // Verify AddOrUpdate was called (entity was prepared)
        Assert.NotNull(addedOrder);
        Assert.Equal(userId, addedOrder.UserId);
        Assert.Equal(externalOrderRef, addedOrder.ExternalOrderRef);

        // Verify SaveChanges was NOT called due to failure
        mockUnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);

        // Verify transaction was started and disposed (rolled back)
        mockUnitOfWork.Verify(x => x.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()), Times.Once);
        mockTransaction.Verify(x => x.Dispose(), Times.Once);

        // Verify commit was never called
        mockUnitOfWork.Verify(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_UnexpectedFailure_AfterSave_When_CreatingOrder_Then_ThrowsException_And_TransactionRolledBack()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var externalOrderRef = "TEST-ORDER-002";
        var command = new CreateOrderCommand
        {
            UserId = userId,
            ExternalOrderRef = externalOrderRef,
            TotalAmount = 200.00m,
            Currency = "USD",
            Notes = "Test order with failure after save"
        };

        var failureInjector = new ConfigurableFailureInjector();
        failureInjector.ConfigureFailure("AfterSave", new InvalidOperationException("Simulated failure after save"));

        var mockRepository = new Mock<IRepository<Order, Guid>>();
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        var mockTransaction = new Mock<IDisposable>();

        mockRepository
            .Setup(x => x.AddOrUpdateAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockUnitOfWork
            .Setup(x => x.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockTransaction.Object);

        mockUnitOfWork
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(1));

        var handler = new CreateOrderCommandHandler(
            mockRepository.Object,
            mockUnitOfWork.Object,
            failureInjector);

        var logger = Mock.Of<ILogger<TransactionalCommandDecorator<CreateOrderCommand>>>();
        var attribute = new TransactionalAttribute { IsolationLevel = IsolationLevel.ReadCommitted };
        var decorator = new TransactionalCommandDecorator<CreateOrderCommand>(
            handler,
            mockUnitOfWork.Object,
            logger,
            attribute);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await decorator.HandleAsync(command, CancellationToken.None));

        Assert.Equal("Simulated failure after save", exception.Message);

        // Verify SaveChanges WAS called before the failure
        mockUnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        // Verify transaction was started and disposed (rolled back)
        mockUnitOfWork.Verify(x => x.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()), Times.Once);
        mockTransaction.Verify(x => x.Dispose(), Times.Once);

        // Verify commit was never called due to exception after save
        mockUnitOfWork.Verify(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_SuccessfulOrder_When_CreatingOrder_Then_CommitsTransaction_And_PersistsOrder()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var externalOrderRef = "TEST-ORDER-003";
        var command = new CreateOrderCommand
        {
            UserId = userId,
            ExternalOrderRef = externalOrderRef,
            TotalAmount = 150.00m,
            Currency = "EUR",
            Notes = "Successful test order"
        };

        var failureInjector = new NoOpFailureInjector(); // No failures configured

        var mockRepository = new Mock<IRepository<Order, Guid>>();
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        var mockTransaction = new Mock<IDisposable>();

        Order persistedOrder = null;
        mockRepository
            .Setup(x => x.AddOrUpdateAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Callback<Order, CancellationToken>((order, ct) => persistedOrder = order)
            .Returns(Task.CompletedTask);

        mockUnitOfWork
            .Setup(x => x.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockTransaction.Object);

        mockUnitOfWork
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(1));

        mockUnitOfWork
            .Setup(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new CreateOrderCommandHandler(
            mockRepository.Object,
            mockUnitOfWork.Object,
            failureInjector);

        var logger = Mock.Of<ILogger<TransactionalCommandDecorator<CreateOrderCommand>>>();
        var attribute = new TransactionalAttribute { IsolationLevel = IsolationLevel.ReadCommitted };
        var decorator = new TransactionalCommandDecorator<CreateOrderCommand>(
            handler,
            mockUnitOfWork.Object,
            logger,
            attribute);

        // Act
        await decorator.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.NotNull(persistedOrder);
        Assert.Equal(userId, persistedOrder.UserId);
        Assert.Equal(externalOrderRef, persistedOrder.ExternalOrderRef);
        Assert.Equal(150.00m, persistedOrder.TotalAmount);
        Assert.Equal("EUR", persistedOrder.Currency);
        Assert.Equal("Pending", persistedOrder.Status);
        Assert.NotNull(persistedOrder.OrderNumber);
        Assert.StartsWith("ORD-", persistedOrder.OrderNumber);

        // Verify complete transaction flow
        mockUnitOfWork.Verify(x => x.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()), Times.Once);
        mockRepository.Verify(x => x.AddOrUpdateAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()), Times.Once);
        mockUnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockUnitOfWork.Verify(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockTransaction.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Given_DbUpdateException_When_CreatingOrder_Then_TransactionRolledBack_And_ExceptionPropagated()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var externalOrderRef = "TEST-ORDER-004";
        var command = new CreateOrderCommand
        {
            UserId = userId,
            ExternalOrderRef = externalOrderRef,
            TotalAmount = 250.00m,
            Currency = "GBP"
        };

        var failureInjector = new NoOpFailureInjector();

        var mockRepository = new Mock<IRepository<Order, Guid>>();
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        var mockTransaction = new Mock<IDisposable>();

        mockRepository
            .Setup(x => x.AddOrUpdateAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockUnitOfWork
            .Setup(x => x.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockTransaction.Object);

        // Simulate database unique constraint violation
        mockUnitOfWork
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unique constraint violation"));

        var handler = new CreateOrderCommandHandler(
            mockRepository.Object,
            mockUnitOfWork.Object,
            failureInjector);

        var logger = Mock.Of<ILogger<TransactionalCommandDecorator<CreateOrderCommand>>>();
        var attribute = new TransactionalAttribute { IsolationLevel = IsolationLevel.ReadCommitted };
        var decorator = new TransactionalCommandDecorator<CreateOrderCommand>(
            handler,
            mockUnitOfWork.Object,
            logger,
            attribute);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await decorator.HandleAsync(command, CancellationToken.None));

        // Verify transaction rollback
        mockUnitOfWork.Verify(x => x.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()), Times.Once);
        mockUnitOfWork.Verify(x => x.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        mockTransaction.Verify(x => x.Dispose(), Times.Once);
    }
}
