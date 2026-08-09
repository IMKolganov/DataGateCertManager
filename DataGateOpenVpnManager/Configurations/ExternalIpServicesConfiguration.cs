namespace DataGateOpenVpnManager.Configurations;

public static class ExternalIpServicesConfiguration
{
    public static void ConfigureExternalIpServices(this WebApplicationBuilder builder)
    {
        builder.Configuration
            .AddJsonFile("externalipsettings.json", optional: true, reloadOnChange: true);
    }
}
