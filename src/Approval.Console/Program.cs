using System.Net;
using System.Text.Json.Serialization;
using Approval.Console;
using Approval.Console.Middleware;
using Approval.Contracts;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Converters;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
SchedulerConnection connection = SchedulerConnection.FromEnvironment();
string mode = connection.Authentication == "None" ? "EMULATOR" : "AZURE";
string portText = Environment.GetEnvironmentVariable("CONSOLE_PORT") ?? "5080";
if (!int.TryParse(portText, out int port) || port is < 1024 or > 65535)
{
    throw new ArgumentException("CONSOLE_PORT must be 1024-65535.");
}
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
builder.Services.AddSingleton<DataConverter>(new JsonDataConverter(ApprovalJson.CreateSerializerOptions()));
builder.Services.AddDurableTaskClient(options => options.UseDurableTaskScheduler(connection.ConnectionString));
builder.Services.AddScoped<IApprovalConsoleService, ApprovalConsoleService>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ConsoleExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddRequestTimeouts(options => options.DefaultPolicy = new() { Timeout = TimeSpan.FromSeconds(60) });
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.AllowDuplicateProperties = false;
});
string dashboard = mode == "EMULATOR"
    ? $"http://localhost:8082/subscriptions/local/schedulers/emulator/taskhubs/{Uri.EscapeDataString(connection.TaskHub)}/orchestrations"
    : $"https://dashboard.durabletask.io/?endpoint={Uri.EscapeDataString(connection.Endpoint)}&taskhub={Uri.EscapeDataString(connection.TaskHub)}";
string? configuredDashboard = Environment.GetEnvironmentVariable("DASHBOARD_URL");
if (configuredDashboard is not null)
{
    if (!Uri.TryCreate(configuredDashboard, UriKind.Absolute, out Uri? uri) ||
        (mode == "AZURE" && (uri.Scheme != "https" || uri.Host != "dashboard.durabletask.io")) ||
        (mode == "EMULATOR" && (!uri.IsLoopback || uri.Scheme != "http")))
    {
        throw new ArgumentException("DASHBOARD_URL must be the Azure dashboard or a loopback emulator URL.");
    }

    dashboard = configuredDashboard;
}
WebApplication app = builder.Build();
app.UseMiddleware<LoopbackOnlyMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRequestTimeouts();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOpenApi();
app.MapApprovals(new(mode, connection.Endpoint, connection.TaskHub, dashboard,
    "Spike only: Acting as is NOT authentication. Never publish or port-forward this console."));
await app.RunAsync();

public partial class Program;
