using System.Threading.Tasks;

namespace ClassifiedAds.Application.Common.Testing;

/// <summary>
/// No-op implementation of IFailureInjector used in production.
/// Does nothing and allows normal execution flow.
/// </summary>
public class NoOpFailureInjector : IFailureInjector
{
    public Task InjectAsync(string injectionPoint)
    {
        // No-op: do nothing in production
        return Task.CompletedTask;
    }
}
