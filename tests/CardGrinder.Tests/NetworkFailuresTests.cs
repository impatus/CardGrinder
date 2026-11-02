using System.Net;
using CardGrinder.Core;
using Xunit;

namespace CardGrinder.Tests;

public sealed class NetworkFailuresTests
{
    [Fact]
    public void OnlyTransientNetworkFailuresAreRetried()
    {
        Assert.True(NetworkFailures.IsTransient(new IOException()));
        Assert.True(NetworkFailures.IsTransient(new TimeoutException()));
        Assert.True(NetworkFailures.IsTransient(new HttpRequestException("Network disconnected")));
        Assert.True(NetworkFailures.IsTransient(new TaskCanceledException("HTTP timeout", new TimeoutException())));
        Assert.False(NetworkFailures.IsTransient(new OperationCanceledException()));
        Assert.False(NetworkFailures.IsTransient(new InvalidDataException("Badges changed")));
        Assert.False(NetworkFailures.IsTransient(new InvalidOperationException("Session expired")));
        Assert.False(NetworkFailures.IsTransient(new HttpRequestException("Rejected", null, HttpStatusCode.Forbidden)));
        Assert.False(NetworkFailures.IsTransient(new HttpRequestException("Rate limited", null, HttpStatusCode.TooManyRequests)));
    }
}
