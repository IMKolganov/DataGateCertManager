using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;

namespace DataGateOpenVpnManager.Configurations;

public static class WebHostConfiguration
{
    public static void ConfigureWebHost(this WebApplicationBuilder builder)
    {
        builder.WebHost.UseSockets(options =>
        {
            // Default write buffer is 64 KiB — UDP→WS batches wait on TCP ACKs under load.
            options.IOQueueCount = Math.Max(1, Environment.ProcessorCount);
            options.MaxWriteBufferSize = 1024 * 1024;
            options.MaxReadBufferSize = 1024 * 1024;
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            var proxyMode = (Environment.GetEnvironmentVariable("OPENVPN_WSS_UDP_PROXY") ?? "dotnet")
                .Trim()
                .ToLowerInvariant();

            if (proxyMode == "rust")
            {
                // Nginx front owns public API_PORT; Kestrel stays on loopback.
                var internalPort = int.Parse(
                    Environment.GetEnvironmentVariable("INTERNAL_DOTNET_PORT") ?? "18080");
                options.Listen(IPAddress.Loopback, internalPort, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                });
                return;
            }

            var port = int.Parse(Environment.GetEnvironmentVariable("API_PORT") ?? "5010");
            options.ListenAnyIP(port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
            });
        });
    }
}
