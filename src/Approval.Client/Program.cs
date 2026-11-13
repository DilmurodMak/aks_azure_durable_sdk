using System.Text.Json;
using Approval.Client;
using Approval.Contracts;
using Azure.Identity;
using Grpc.Core;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Converters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine(ClientCommand.Usage);
    return args.Length == 0 ? 2 : 0;
}

using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(60));
Console.CancelKeyPress += (_, cancel) =>
{
    cancel.Cancel = true;
    cancellation.Cancel();
};

try
{
    ClientCommand command = ClientCommand.Parse(args);
    SchedulerConnection connection = SchedulerConnection.FromEnvironment();
    ServiceCollection services = new();
    services.AddSingleton<DataConverter>(new JsonDataConverter(ApprovalJson.CreateSerializerOptions()));
    services.AddLogging(logging => logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    }));
    services.AddDurableTaskClient(builder => builder.UseDurableTaskScheduler(connection.ConnectionString));
    await using ServiceProvider provider = services.BuildServiceProvider();
    DurableTaskClient client = provider.GetRequiredService<DurableTaskClient>();
    object result = await ClientOperations.ExecuteAsync(client, command, cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(result, ClientOperations.JsonOptions));
    return 0;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"Input/configuration error: {exception.Message}\n{ClientCommand.Usage}");
    return 2;
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine($"Request rejected: {exception.Message}");
    return 1;
}
catch (RpcException exception)
{
    Console.Error.WriteLine($"Scheduler error ({exception.StatusCode}): {exception.Status.Detail}");
    return 1;
}
catch (AuthenticationFailedException exception)
{
    Console.Error.WriteLine($"Authentication error: {exception.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Request canceled or exceeded the 60-second client deadline. Query status before retrying a start or decision.");
    return 1;
}
