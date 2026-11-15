using Approval.Contracts;
using Approval.Worker;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.DurableTask.Converters;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));

SchedulerConnection connection;
try
{
    connection = SchedulerConnection.FromEnvironment();
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"Configuration error: {exception.Message}");
    return 2;
}

// The SDK's orchestration/activity spans come from "Microsoft.DurableTask"; Approval.Spike adds the business tags.
string? insights = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("approval-worker"))
    .WithTracing(tracing =>
    {
        tracing.AddSource("Microsoft.DurableTask", ApprovalTelemetry.SourceName);
        if (!string.IsNullOrWhiteSpace(insights)) tracing.AddAzureMonitorTraceExporter(options => options.ConnectionString = insights);
    });

builder.Services.AddDurableTaskWorker()
    .Configure(options => options.DataConverter = new JsonDataConverter(ApprovalJson.CreateSerializerOptions()))
    .AddTasks(tasks =>
    {
        tasks.AddOrchestrator<ApprovalStep>();
        tasks.AddOrchestrator<ApprovalChain>();
        tasks.AddActivity<RecordSubmission>();
        tasks.AddActivity<RecordDecision>();
        tasks.AddActivity<ApplyDecision>();
    })
    .UseDurableTaskScheduler(connection.ConnectionString);

using IHost host = builder.Build();
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup")
    .LogInformation("Worker starting: endpoint={Endpoint}, taskHub={TaskHub}, authentication={Authentication}",
        connection.Endpoint, connection.TaskHub, connection.Authentication);
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup")
    .LogInformation("Tracing: Application Insights export {State}",
        string.IsNullOrWhiteSpace(insights) ? "off (no APPLICATIONINSIGHTS_CONNECTION_STRING)" : "on");

await host.RunAsync();
return 0;
