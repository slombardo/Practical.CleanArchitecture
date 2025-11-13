namespace ClassifiedAds.Application;

/// <summary>
/// No-operation implementation of IFailureInjector for production use.
/// Does nothing when failure points are checked.
/// </summary>
public class NoOpFailureInjector : IFailureInjector
{
    public void CheckForFailure(string injectionPoint)
    {
        // No-op: do nothing in production
    }
}
