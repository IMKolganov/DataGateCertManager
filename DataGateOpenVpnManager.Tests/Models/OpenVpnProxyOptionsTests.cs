using DataGateOpenVpnManager.Models;

namespace DataGateOpenVpnManager.Tests.Models;

public class OpenVpnProxyOptionsTests
{
    [Fact]
    public void IsSessionAuditEnabled_WhenByteDebugOrSessionAudit()
    {
        Assert.True(new OpenVpnProxyOptions { ByteDebug = true }.IsSessionAuditEnabled);
        Assert.True(new OpenVpnProxyOptions { SessionAudit = true }.IsSessionAuditEnabled);
        Assert.False(new OpenVpnProxyOptions().IsSessionAuditEnabled);
    }

    [Theory]
    [InlineData(true, 10, true, false, true)]
    [InlineData(false, 10, true, false, true)]
    [InlineData(true, 0, false, false, false)]
    [InlineData(false, 0, false, false, false)]
    [InlineData(false, 0, false, true, true)]
    public void NeedsBackgroundManagementRefresh_ReflectsZombieByteDebugAndPiHole(
        bool byteDebug,
        int byteDebugInterval,
        bool zombieEnabled,
        bool piHoleEnabled,
        bool expected)
    {
        var options = new OpenVpnProxyOptions
        {
            ByteDebug = byteDebug,
            ByteDebugIntervalSeconds = byteDebugInterval,
            CloseZombieAfterMissingSeconds = zombieEnabled ? 60 : 0
        };

        Assert.Equal(expected, options.NeedsBackgroundManagementRefresh(piHoleEnabled));
    }
}
