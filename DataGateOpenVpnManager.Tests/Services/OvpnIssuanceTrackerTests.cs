using DataGateOpenVpnManager.Services;
using DataGateOpenVpnManager.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataGateOpenVpnManager.Tests.Services;

public class OvpnIssuanceTrackerTests
{
    private static OvpnIssuanceTracker CreateTracker(int waitTimeoutSeconds = 5) =>
        new(Options.Create(new OvpnIssuanceOptions { WaitTimeoutSeconds = waitTimeoutSeconds }),
            NullLogger<OvpnIssuanceTracker>.Instance);

    [Fact]
    public async Task WaitUntilReadyAsync_WhenFileAppearsAfterBegin_Completes()
    {
        var tracker = CreateTracker();
        var path = Path.Combine(Path.GetTempPath(), "iss_" + Guid.NewGuid().ToString("N") + ".ovpn");

        await using var lease = tracker.Begin("cn1");
        var wait = tracker.WaitUntilReadyAsync("cn1", path, CancellationToken.None);

        await Task.Delay(50);
        await File.WriteAllTextAsync(path, "x");
        lease.MarkCompleted();

        await wait;
        Assert.True(File.Exists(path));
        File.Delete(path);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_WhenIssuanceFails_Throws()
    {
        var tracker = CreateTracker();
        var path = Path.Combine(Path.GetTempPath(), "issfail_" + Guid.NewGuid().ToString("N") + ".ovpn");

        await using var lease = tracker.Begin("cn-fail");
        var wait = tracker.WaitUntilReadyAsync("cn-fail", path, CancellationToken.None);

        lease.MarkFailed(new InvalidOperationException("easyrsa blew up"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
        Assert.Contains("issuance failed", ex.Message);
        Assert.Contains("easyrsa blew up", ex.InnerException!.Message);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_WhenNeverStartedAndNoFile_TimesOut()
    {
        var tracker = CreateTracker(waitTimeoutSeconds: 1);
        var path = Path.Combine(Path.GetTempPath(), "missing_" + Guid.NewGuid().ToString("N") + ".ovpn");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            tracker.WaitUntilReadyAsync("ghost", path, CancellationToken.None));
    }
}
