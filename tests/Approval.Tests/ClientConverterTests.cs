using Approval.Contracts;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Converters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Approval.Tests;

public sealed class ClientConverterTests
{
    [Fact]
    public async Task ClientResolvesTheSharedCaseInsensitiveDataConverter()
    {
        ServiceCollection services = new();
        DataConverter converter = new JsonDataConverter(ApprovalJson.CreateSerializerOptions());
        services.AddSingleton(converter);
        services.AddLogging();
        services.AddDurableTaskClient(options =>
            options.UseDurableTaskScheduler("Endpoint=http://localhost:8080;TaskHub=default;Authentication=None"));
        await using ServiceProvider provider = services.BuildServiceProvider();
        DurableTaskClient client = provider.GetRequiredService<DurableTaskClient>();
        DurableTaskClientOptions options = provider.GetRequiredService<IOptionsMonitor<DurableTaskClientOptions>>().Get(client.Name);
        Assert.Same(converter, options.DataConverter);
        Assert.Equal(new Decision("finance", "Approve", 3),
            options.DataConverter.Deserialize<Decision>("{\"approver\":\"finance\",\"outcome\":\"Approve\",\"version\":3}"));
        Assert.Contains("\"Approver\"", options.DataConverter.Serialize(new Decision("finance", "Approve", 3)));
    }
}
