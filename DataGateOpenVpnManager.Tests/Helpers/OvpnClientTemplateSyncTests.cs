using DataGateOpenVpnManager.Helpers;
using Microsoft.Extensions.Configuration;

namespace DataGateOpenVpnManager.Tests.Helpers;

public class OvpnClientTemplateSyncTests
{
    [Fact]
    public void Apply_Replaces_Stale_Cbc_Proto_Auth_Tls_And_Inserts_DataCiphers()
    {
        const string template = """
            client
            proto tcp
            remote-cert-tls server
            tls-version-min 1.0
            cipher AES-256-CBC
            auth SHA1
            verb 1
            """;

        var result = OvpnClientTemplateSync.Apply(template, new OvpnClientTemplateSettings
        {
            Proto = "udp",
            Cipher = "AES-128-GCM",
            DataCiphers = "AES-128-GCM:AES-256-GCM:CHACHA20-POLY1305",
            Auth = "SHA256",
            TlsVersionMin = "1.2",
            ClientVerb = "3"
        });

        Assert.Contains("proto udp", result);
        Assert.DoesNotContain("proto tcp", result);
        Assert.Contains("cipher AES-128-GCM", result);
        Assert.DoesNotContain("AES-256-CBC", result);
        Assert.Contains("data-ciphers AES-128-GCM:AES-256-GCM:CHACHA20-POLY1305", result);
        Assert.Contains("auth SHA256", result);
        Assert.DoesNotContain("auth SHA1", result);
        Assert.Contains("tls-version-min 1.2", result);
        Assert.Contains("verb 3", result);
        Assert.Contains("remote-cert-tls server", result);
    }

    [Fact]
    public void Apply_NoOps_When_Settings_Empty()
    {
        const string template = "cipher AES-256-CBC\n";
        var result = OvpnClientTemplateSync.Apply(template, new OvpnClientTemplateSettings());
        Assert.Equal(template, result);
    }

    [Fact]
    public void Apply_Preserves_AuthNocache_And_Removes_Stale_DataCiphers_When_Unset()
    {
        const string template = """
            cipher AES-128-GCM
            data-ciphers AES-128-GCM:CHACHA20-POLY1305
            auth SHA256
            auth-nocache
            verb 3
            """;

        var result = OvpnClientTemplateSync.Apply(template, new OvpnClientTemplateSettings
        {
            Cipher = "AES-256-CBC",
            DataCiphers = null,
            Auth = "SHA256"
        });

        Assert.Contains("cipher AES-256-CBC", result);
        Assert.DoesNotContain("data-ciphers", result);
        Assert.Contains("auth SHA256", result);
        Assert.Contains("auth-nocache", result);
    }

    [Fact]
    public void FromConfiguration_Defaults_Cipher_For_Dco_When_Env_Empty()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DCO"] = "true",
            ["PROTO"] = "udp"
        }).Build();

        var settings = OvpnNodeClientSettings.FromConfiguration(config);

        Assert.Equal("AES-128-GCM", settings.Cipher);
        Assert.Equal("AES-128-GCM:AES-256-GCM:CHACHA20-POLY1305", settings.DataCiphers);
        Assert.Equal("udp", settings.Proto);
        Assert.Equal("SHA256", settings.Auth);
        Assert.Equal("1.2", settings.TlsVersionMin);
        Assert.Equal("3", settings.ClientVerb);
    }
}
