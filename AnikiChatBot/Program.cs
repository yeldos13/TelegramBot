using AnikiChatBot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

FileLog.Install(Path.Combine(AppContext.BaseDirectory, "logs"), echoToConsole: !WindowsServiceHelpers.IsWindowsService());

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.ColorBehavior = LoggerColorBehavior.Disabled;
});

builder.Services.AddWindowsService(options => options.ServiceName = "AnikiChatBot");
builder.Services.AddHostedService<BotWorker>();

await builder.Build().RunAsync();
