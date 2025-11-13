using System.Threading.Tasks;

namespace ClassifiedAds.Application.Common.Testing;

/// <summary>
/// Seam for injecting failures at specific points in command handlers for testing purposes.
/// Production code uses NoOpFailureInjector. Integration tests can provide test doubles
/// to simulate various failure scenarios (e.g., "BeforeSave", "AfterDomainEvents").
/// </summary>
public interface IFailureInjector
{
    /// <summary>
    /// Injects a failure at the specified injection point if configured.
    /// </summary>
    /// <param name="injectionPoint">The named point where failure can be injected (e.g., "BeforeSave", "AfterDomainEvents")</param>
    Task InjectAsync(string injectionPoint);
}
