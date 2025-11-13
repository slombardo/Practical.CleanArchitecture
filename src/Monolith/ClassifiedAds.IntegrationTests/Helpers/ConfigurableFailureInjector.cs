using ClassifiedAds.Application.Common.Testing;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClassifiedAds.IntegrationTests.Helpers;

/// <summary>
/// Test double for IFailureInjector that allows configuring specific injection points to throw exceptions.
/// Used in integration tests to simulate failure scenarios.
/// </summary>
public class ConfigurableFailureInjector : IFailureInjector
{
    private readonly Dictionary<string, Exception> _configuredFailures = new();

    /// <summary>
    /// Configures a failure to be thrown at the specified injection point.
    /// </summary>
    /// <param name="injectionPoint">The injection point name (e.g., "BeforeSave", "AfterSave")</param>
    /// <param name="exception">The exception to throw</param>
    public void ConfigureFailure(string injectionPoint, Exception exception)
    {
        _configuredFailures[injectionPoint] = exception;
    }

    /// <summary>
    /// Clears all configured failures.
    /// </summary>
    public void ClearFailures()
    {
        _configuredFailures.Clear();
    }

    public Task InjectAsync(string injectionPoint)
    {
        if (_configuredFailures.TryGetValue(injectionPoint, out var exception))
        {
            throw exception;
        }

        return Task.CompletedTask;
    }
}
