namespace ClassifiedAds.Application;

/// <summary>
/// Interface for injecting failures at specific points for testing purposes.
/// Production code should use the no-op implementation.
/// </summary>
public interface IFailureInjector
{
    /// <summary>
    /// Checks if a failure should be triggered at the specified injection point.
    /// </summary>
    /// <param name="injectionPoint">The name of the injection point (e.g., "BeforeSave", "AfterDomainEvents").</param>
    void CheckForFailure(string injectionPoint);
}
