using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        // Reuse sockets; set sane timeout
        services.AddHttpClient("mcp", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
        });

        services.AddSingleton<McpToolSanitizer>();
        services.AddSingleton<ProxyConfig>();
    })
    .ConfigureLogging(logging =>
    {
        logging.AddConsole();
    })
    .Build();

host.Run();
