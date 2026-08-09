using System.Threading.RateLimiting;
using DataGateOpenVpnManager.Helpers;
using DataGateOpenVpnManager.Services;
using DataGateOpenVpnManager.Services.EasyRsaServices;
using DataGateOpenVpnManager.Services.EasyRsaServices.Interfaces;
using DataGateOpenVpnManager.Services.Interfaces;

namespace DataGateOpenVpnManager.Configurations;

public static class ServiceConfiguration
{
    public static void ConfigureServices(this IServiceCollection services, IConfiguration config)
    {
        // Core services
        services.AddScoped<IOvpnFileService, OvpnFileService>();

        // EasyRsa services
        services.AddScoped<IEasyRsaService, EasyRsaService>();
        services.AddScoped<IEasyRsaParseDbService, EasyRsaParseDbService>();
        services.AddScoped<IBashCommandRunner, BashCommandRunner>();

        // OpenVpn services
        services.AddScoped<IOpenVpnServerService, OpenVpnServerService>();

        // Rate Limiting
        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.User?.Identity?.Name ?? context.Request.Headers.Host.ToString(),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1)
                    }));
        });

        services.ConfigureEasyRsa(config);

        services.AddMemoryCache();
        services.AddHttpClient<IExternalIpAddressService, ExternalIpAddressService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // HttpClient for MicroserviceJwtValidator
        services.AddHttpClient<MicroserviceJwtValidator>(client =>
        {
            var baseUrl = config["Backend:BaseUrl"];
            client.BaseAddress = new Uri(baseUrl ?? throw new InvalidOperationException("Backend:BaseUrl is required"));
        });

        services.AddSingleton<IMicroserviceJwtValidator>(sp =>
        {
            var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(MicroserviceJwtValidator));
            var logger = sp.GetRequiredService<ILogger<MicroserviceJwtValidator>>();
            return new MicroserviceJwtValidator(client, logger);
        });

        services.AddHostedService<MicroserviceJwtValidatorInitializer>();

        services.ConfigureProxy(config);
        services.ConfigurePiHole(config);

        services.AddControllers().AddNewtonsoftJson();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
    }
}
