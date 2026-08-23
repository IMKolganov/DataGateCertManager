using DataGateOpenVpnManager.Middlewares;

namespace DataGateOpenVpnManager.Tests.Security;

/// <summary>
/// Documents OpenVPN manager JWT exclusion / local-only surfaces.
/// /api/proxy is intentionally unauthenticated today — restrict by network in production
/// or require JWT if the node is publicly reachable.
/// </summary>
public class OpenVpnManagerPublicRoutesInventoryTests
{
    private static readonly string[] IntentionalJwtExcludedPrefixes =
    [
        "/",
        "/favicon.ico",
        "/swagger",
        "/swagger/index.html",
        "/swagger/v1/swagger.json",
        "/api/proxy",
    ];

    private static readonly string[] IntentionalLocalOnlyPrefixes =
    [
        "/api/info",
        "/api/diagnostics",
        "/api/vpn-events/connect",
        "/api/vpn-events/disconnect",
        "/api/vpn-events/tlsverify",
        "/api/vpn-events/attempt",
        "/api/vpn-events/envdump",
    ];

    [Fact]
    public void JwtExcludedPaths_MatchDocumentedInventory()
    {
        var field = typeof(JwtValidationMiddleware)
            .GetField("ExcludedPaths", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        var paths = Assert.IsType<string[]>(field!.GetValue(null));
        Assert.Equal(IntentionalJwtExcludedPrefixes.OrderBy(x => x), paths.OrderBy(x => x));
    }

    [Fact]
    public void LocalOnlyPaths_MatchDocumentedInventory()
    {
        var field = typeof(JwtValidationMiddleware)
            .GetField("LocalOnlyPaths", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        var paths = Assert.IsType<string[]>(field!.GetValue(null));
        Assert.Equal(IntentionalLocalOnlyPrefixes.OrderBy(x => x), paths.OrderBy(x => x));
    }

    [Fact]
    public void ProxyPath_IsJwtExcluded_ByDesign()
    {
        Assert.Contains("/api/proxy", IntentionalJwtExcludedPrefixes);
    }
}
