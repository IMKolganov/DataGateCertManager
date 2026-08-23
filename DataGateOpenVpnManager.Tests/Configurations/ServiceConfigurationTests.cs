using DataGateOpenVpnManager.Configurations;
using DataGateOpenVpnManager.Helpers;
using DataGateOpenVpnManager.Services;
using DataGateOpenVpnManager.Services.EasyRsaServices.Interfaces;
using DataGateOpenVpnManager.Services.Interfaces;
using DataGateOpenVpnManager.Services.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DataGateOpenVpnManager.Tests.Configurations;

public class ServiceConfigurationTests
{
    [Fact]
    public void ConfigureServices_RegistersScopedServices()
    {
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?> { ["Backend:BaseUrl"] = "http://localhost:9999/" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData!).Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();

        services.ConfigureServices(config);
        var provider = services.BuildServiceProvider();

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var ovpn1 = scope1.ServiceProvider.GetService<IOvpnFileService>();
        var ovpn2 = scope2.ServiceProvider.GetService<IOvpnFileService>();
        Assert.NotNull(ovpn1);
        Assert.NotNull(ovpn2);
        Assert.NotSame(ovpn1, ovpn2);

        var easyRsa1 = scope1.ServiceProvider.GetService<IEasyRsaService>();
        var easyRsa2 = scope2.ServiceProvider.GetService<IEasyRsaService>();
        Assert.NotNull(easyRsa1);
        Assert.NotSame(easyRsa1, easyRsa2);

        var openVpnServer = scope1.ServiceProvider.GetService<IOpenVpnServerService>();
        Assert.NotNull(openVpnServer);

        var process1 = provider.GetService<IOpenVpnProcessService>();
        var process2 = provider.GetService<IOpenVpnProcessService>();
        Assert.NotNull(process1);
        Assert.Same(process1, process2);
        Assert.NotNull(provider.GetService<IOpenVpnProcessRunner>());
    }

    [Fact]
    public void ConfigureServices_RegistersSingletonEasyRsaPathResolver()
    {
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?> { ["Backend:BaseUrl"] = "http://localhost:9999/" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData!).Build();
        services.AddSingleton<IConfiguration>(config);

        services.ConfigureServices(config);
        var provider = services.BuildServiceProvider();

        var resolver1 = provider.GetService<IEasyRsaPathResolver>();
        var resolver2 = provider.GetService<IEasyRsaPathResolver>();
        Assert.NotNull(resolver1);
        Assert.Same(resolver1, resolver2);
    }

    [Fact]
    public void ConfigureServices_RegistersMicroserviceJwtValidator()
    {
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?> { ["Backend:BaseUrl"] = "http://localhost:9999/" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData!).Build();
        services.AddSingleton<IConfiguration>(config);

        services.ConfigureServices(config);
        var provider = services.BuildServiceProvider();

        var validator = provider.GetRequiredService<IMicroserviceJwtValidator>();
        Assert.NotNull(validator);
    }

    [Fact]
    public void ConfigureServices_RegistersProxyTrackingSingletons()
    {
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?> { ["Backend:BaseUrl"] = "http://localhost:9999/" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData!).Build();
        services.AddSingleton<IConfiguration>(config);

        services.ConfigureServices(config);
        var provider = services.BuildServiceProvider();

        var active1 = provider.GetRequiredService<IActiveProxyConnectionService>();
        var active2 = provider.GetRequiredService<IActiveProxyConnectionService>();
        Assert.Same(active1, active2);

        var identity1 = provider.GetRequiredService<IProxyConnectionIdentityResolver>();
        var identity2 = provider.GetRequiredService<IProxyConnectionIdentityResolver>();
        Assert.Same(identity1, identity2);

        var history1 = provider.GetRequiredService<IProxyConnectionHistoryService>();
        var history2 = provider.GetRequiredService<IProxyConnectionHistoryService>();
        Assert.Same(history1, history2);

        var flow1 = provider.GetRequiredService<IProxyTrafficFlowService>();
        var flow2 = provider.GetRequiredService<IProxyTrafficFlowService>();
        Assert.Same(flow1, flow2);
    }

    [Fact]
    public void ConfigureServices_RegistersSingletonEasyRsaPkiMutex()
    {
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?> { ["Backend:BaseUrl"] = "http://localhost:9999/" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData!).Build();
        services.AddSingleton<IConfiguration>(config);

        services.ConfigureServices(config);
        var provider = services.BuildServiceProvider();

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var m1 = scope1.ServiceProvider.GetRequiredService<IEasyRsaPkiMutex>();
        var m2 = scope2.ServiceProvider.GetRequiredService<IEasyRsaPkiMutex>();
        Assert.Same(m1, m2);
    }

    [Fact]
    public void ConfigureServices_RegistersSingletonOvpnIssuanceTracker()
    {
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?> { ["Backend:BaseUrl"] = "http://localhost:9999/" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData!).Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();

        services.ConfigureServices(config);
        var provider = services.BuildServiceProvider();

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var t1 = scope1.ServiceProvider.GetRequiredService<IOvpnIssuanceTracker>();
        var t2 = scope2.ServiceProvider.GetRequiredService<IOvpnIssuanceTracker>();
        Assert.Same(t1, t2);
    }

    [Fact]
    public void ConfigureServices_AppliesOvpnIssuanceWaitTimeoutFromEnv()
    {
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?>
        {
            ["Backend:BaseUrl"] = "http://localhost:9999/",
            [OvpnIssuanceOptions.WaitTimeoutSecondsEnvVar] = "42",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData!).Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();

        services.ConfigureServices(config);
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OvpnIssuanceOptions>>().Value;
        Assert.Equal(42, options.WaitTimeoutSeconds);
    }

    [Fact]
    public void ConfigureServices_OvpnIssuanceWaitTimeoutDefaultsToTen()
    {
        var services = new ServiceCollection();
        var configData = new Dictionary<string, string?> { ["Backend:BaseUrl"] = "http://localhost:9999/" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData!).Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();

        services.ConfigureServices(config);
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OvpnIssuanceOptions>>().Value;
        Assert.Equal(OvpnIssuanceOptions.DefaultWaitTimeoutSeconds, options.WaitTimeoutSeconds);
        Assert.Equal(10, options.WaitTimeoutSeconds);
    }
}
