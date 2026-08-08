using DataGateOpenVpnManager.Configurations;

var builder = WebApplication.CreateBuilder(args);
Console.OutputEncoding = System.Text.Encoding.UTF8;
builder.Host.ConfigureSerilog();
builder.ConfigureExternalIpServices();

builder.Services.ConfigureServices(builder.Configuration);
builder.Services.ConfigureSignalR(builder.Configuration);
builder.ConfigureWebHost();

var app = builder.Build();
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

app.ConfigureMiddleware();
app.ConfigurePipeline();

app.Run();
