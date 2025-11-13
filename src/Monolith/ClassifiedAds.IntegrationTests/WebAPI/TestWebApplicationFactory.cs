using ClassifiedAds.Application.Common.Testing;
using ClassifiedAds.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Linq;

namespace ClassifiedAds.IntegrationTests.WebAPI;

/// <summary>
/// Custom WebApplicationFactory for integration testing.
/// Replaces production database with in-memory database and allows DI overrides.
/// </summary>
/// <typeparam name="TProgram">The Program class from ClassifiedAds.WebAPI</typeparam>
public class TestWebApplicationFactory<TProgram> : WebApplicationFactory<TProgram>
    where TProgram : class
{
    private IFailureInjector _failureInjector;

    public TestWebApplicationFactory()
    {
        // Default to no-op failure injector
        _failureInjector = new NoOpFailureInjector();
    }

    /// <summary>
    /// Configures a custom failure injector for testing specific failure scenarios.
    /// </summary>
    public void ConfigureFailureInjector(IFailureInjector failureInjector)
    {
        _failureInjector = failureInjector;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // Remove existing DbContext registration
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AdsDbContext>));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            // Add in-memory database for testing
            services.AddDbContext<AdsDbContext>((serviceProvider, options) =>
            {
                options.UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}");
            });

            // Replace IFailureInjector with test double
            services.RemoveAll<IFailureInjector>();
            services.AddSingleton(_failureInjector);

            // Ensure database is created
            var serviceProvider = services.BuildServiceProvider();
            using (var scope = serviceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AdsDbContext>();
                dbContext.Database.EnsureCreated();
            }
        });
    }

    /// <summary>
    /// No-op failure injector (production behavior).
    /// </summary>
    private class NoOpFailureInjector : IFailureInjector
    {
        public System.Threading.Tasks.Task InjectAsync(string injectionPoint) => System.Threading.Tasks.Task.CompletedTask;
    }
}
