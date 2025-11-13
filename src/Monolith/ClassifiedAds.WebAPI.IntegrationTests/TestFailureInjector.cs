using ClassifiedAds.Application;
using System;
using System.Collections.Generic;

namespace ClassifiedAds.WebAPI.IntegrationTests;

/// <summary>
/// Test implementation of IFailureInjector that allows configuring failures at specific injection points.
/// </summary>
public class TestFailureInjector : IFailureInjector
{
    private readonly Dictionary<string, Exception> _failures = new();

    /// <summary>
    /// Configures a failure to be thrown at the specified injection point.
    /// </summary>
    /// <param name="injectionPoint">The injection point name.</param>
    /// <param name="exception">The exception to throw.</param>
    public void ConfigureFailure(string injectionPoint, Exception exception)
    {
        _failures[injectionPoint] = exception;
    }

    /// <summary>
    /// Removes a configured failure.
    /// </summary>
    /// <param name="injectionPoint">The injection point name.</param>
    public void ClearFailure(string injectionPoint)
    {
        _failures.Remove(injectionPoint);
    }

    /// <summary>
    /// Clears all configured failures.
    /// </summary>
    public void ClearAllFailures()
    {
        _failures.Clear();
    }

    public void CheckForFailure(string injectionPoint)
    {
        if (_failures.TryGetValue(injectionPoint, out var exception))
        {
            throw exception;
        }
    }
}
