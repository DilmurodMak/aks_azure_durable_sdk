using Approval.Client;
using Approval.Contracts;

namespace Approval.Tests;

public sealed class ClientAndConfigurationTests
{
    [Fact]
    public void StartParsesTheRequestedSunnySodaInput()
    {
        ClientCommand command = ClientCommand.Parse([
            "start", "--instance", "sunny-soda-v3-001", "--plan", "sunny-soda", "--version", "3",
            "--requester", "requester", "--approver", "finance"
        ]);
        Assert.Equal(new ApprovalRequest("sunny-soda", 3, "requester", "finance", 15), command.Request);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void BlankInputCannotApprove(string outcome)
    {
        Assert.Throws<ArgumentException>(() => ClientCommand.Parse([
            "decide", "--instance", "test", "--approver", "finance", "--version", "3", "--outcome", outcome
        ]));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1441")]
    public void InvalidTimeoutIsAnExplicitError(string timeout)
    {
        Assert.Throws<ArgumentException>(() => ClientCommand.Parse([
            "start", "--instance", "test", "--plan", "sunny-soda", "--version", "3",
            "--requester", "requester", "--approver", "finance", "--timeout-minutes", timeout
        ]));
    }

    [Theory]
    [InlineData("status", "--instance", "test", "--unknown")]
    [InlineData("status", "--instance", "test", "--instance", "again")]
    [InlineData("decide", "--instance", "test")]
    public void MissingDuplicateOrUnknownOptionsFail(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ClientCommand.Parse(args));
    }

    [Fact]
    public void EmptyCommandIsAnError() => Assert.Throws<ArgumentException>(() => ClientCommand.Parse([]));

    [Theory]
    [InlineData("https://scheduler.example", "AzureCli", "AzureCli")]
    [InlineData("http://localhost:8080", "None", "None")]
    public void ValidConnectionHasExplicitAuthentication(string endpoint, string authentication, string expected)
    {
        SchedulerConnection connection = Connection(endpoint, authentication);
        Assert.Contains("Authentication=" + expected, connection.ConnectionString);
        Assert.Contains("TaskHub=default", connection.ConnectionString);
    }

    [Theory]
    [InlineData("https://scheduler.example", "None")]
    [InlineData("http://scheduler.example", "AzureCli")]
    [InlineData("https://scheduler.example;Authentication=None", "AzureCli")]
    [InlineData("https://scheduler.example/path", "AzureCli")]
    [InlineData("https://scheduler.example", "ManagedIdentity")]
    public void UnsafeOrUnsupportedConnectionsFail(string endpoint, string authentication)
    {
        Assert.Throws<ArgumentException>(() => Connection(endpoint, authentication));
    }

    [Fact]
    public void MissingConfigurationDoesNotFallBackToTheEmulator()
    {
        Assert.Throws<ArgumentException>(() => SchedulerConnection.Create(_ => null));
    }

    [Fact]
    public void WorkloadIdentityRequiresFederatedTokenConfiguration()
    {
        Assert.Throws<ArgumentException>(() => Connection("https://scheduler.example", "WorkloadIdentity"));
        Dictionary<string, string> environment = new()
        {
            ["ENDPOINT"] = "https://scheduler.example",
            ["TASKHUB"] = "default",
            ["SCHEDULER_AUTH"] = "WorkloadIdentity",
            ["AZURE_CLIENT_ID"] = "11111111-1111-1111-1111-111111111111",
            ["AZURE_TENANT_ID"] = "22222222-2222-2222-2222-222222222222",
            ["AZURE_FEDERATED_TOKEN_FILE"] = "/var/run/secrets/azure/tokens/azure-identity-token"
        };
        Assert.Equal("WorkloadIdentity", SchedulerConnection.Create(key => environment.GetValueOrDefault(key)).Authentication);
    }

    private static SchedulerConnection Connection(string endpoint, string authentication)
    {
        Dictionary<string, string> environment = new()
        {
            ["ENDPOINT"] = endpoint,
            ["TASKHUB"] = "default",
            ["SCHEDULER_AUTH"] = authentication
        };
        return SchedulerConnection.Create(key => environment.GetValueOrDefault(key));
    }
}
