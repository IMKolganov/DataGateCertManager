using DataGateOpenVpnManager.Helpers;
using Microsoft.Extensions.Configuration;

namespace DataGateOpenVpnManager.Tests.Helpers;

public class VpnServerAnnounceApiUrlResolverTests
{
    [Fact]
    public void Resolve_PublicApiUrl_WinsOverPublicIpAndPort()
    {
        var result = VpnServerAnnounceApiUrlResolver.Resolve(
            "https://vpn.example.com:9443",
            "203.0.113.10",
            5010);

        Assert.Equal("https://vpn.example.com:9443/", result);
    }

    [Fact]
    public void Resolve_PublicApiUrl_PreservesTrailingSlash()
    {
        var result = VpnServerAnnounceApiUrlResolver.Resolve(
            "https://vpn.example.com/",
            "203.0.113.10",
            5010);

        Assert.Equal("https://vpn.example.com/", result);
    }

    [Fact]
    public void Resolve_WithoutPublicApiUrl_BuildsFromIpAndPort()
    {
        var result = VpnServerAnnounceApiUrlResolver.Resolve(null, "203.0.113.10", 5011);

        Assert.Equal("http://203.0.113.10:5011/", result);
    }

    [Fact]
    public void Resolve_WhitespacePublicApiUrl_FallsBackToIp()
    {
        var result = VpnServerAnnounceApiUrlResolver.Resolve("   ", "198.51.100.2", 5010);

        Assert.Equal("http://198.51.100.2:5010/", result);
    }

    [Fact]
    public void Resolve_MissingIp_ReturnsNull()
    {
        Assert.Null(VpnServerAnnounceApiUrlResolver.Resolve(null, null, 5010));
        Assert.Null(VpnServerAnnounceApiUrlResolver.Resolve(null, "  ", 5010));
    }

    [Fact]
    public void ResolveApiPort_EnvWinsOverConfiguration()
    {
        var previous = Environment.GetEnvironmentVariable("API_PORT");
        try
        {
            Environment.SetEnvironmentVariable("API_PORT", "5099");
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["API_PORT"] = "5012" })
                .Build();

            Assert.Equal(5099, VpnServerAnnounceApiUrlResolver.ResolveApiPort(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable("API_PORT", previous);
        }
    }

    [Fact]
    public void ResolveApiPort_UsesConfigurationWhenEnvUnset()
    {
        var previous = Environment.GetEnvironmentVariable("API_PORT");
        try
        {
            Environment.SetEnvironmentVariable("API_PORT", null);
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["API_PORT"] = "5012" })
                .Build();

            Assert.Equal(5012, VpnServerAnnounceApiUrlResolver.ResolveApiPort(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable("API_PORT", previous);
        }
    }

    [Fact]
    public void GetConfiguredPublicApiUrl_EnvWinsOverConfiguration()
    {
        var previous = Environment.GetEnvironmentVariable(VpnServerAnnounceApiUrlResolver.PublicApiUrlKey);
        try
        {
            Environment.SetEnvironmentVariable(
                VpnServerAnnounceApiUrlResolver.PublicApiUrlKey,
                "https://from-env.example/");
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PUBLIC_API_URL"] = "https://from-config.example/"
                })
                .Build();

            Assert.Equal(
                "https://from-env.example/",
                VpnServerAnnounceApiUrlResolver.GetConfiguredPublicApiUrl(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable(VpnServerAnnounceApiUrlResolver.PublicApiUrlKey, previous);
        }
    }
}
