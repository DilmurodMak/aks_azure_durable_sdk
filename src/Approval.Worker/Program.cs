using Approval.Contracts;
using Approval.Worker;
using Microsoft.DurableTask.Converters;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

builder.Services.AddDurableTaskWorker()
    .Configure(options => options.DataConverter = new JsonDataConverter(ApprovalJson.CreateSerializerOptions()))
    .AddTasks(tasks =>
    {
        tasks.AddOrchestrator<PlanApproval>();
        tasks.AddActivity<RecordSubmission>();
        tasks.AddActivity<ApplyDecision>();
    })
    .UseDurableTaskScheduler(connection.ConnectionString);

using IHost host = builder.Build();
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup")
    .LogInformation("Worker starting: endpoint={Endpoint}, taskHub={TaskHub}, authentication={Authentication}",
        connection.Endpoint, connection.TaskHub, connection.Authentication);

await host.RunAsync();
return 0;
