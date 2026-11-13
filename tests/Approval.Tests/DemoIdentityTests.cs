using System.Text.Json;
using Approval.Console;
using Approval.Contracts;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Moq;

namespace Approval.Tests;

public sealed class DemoIdentityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"demo-identity-test-{Guid.NewGuid():N}");
    private PolicyStore Policies => new(Path.Combine(directory, "policies.json"));
    private DemoIdentityStore Identities => new(Path.Combine(directory, "identities.json"));

    [Fact]
    public async Task PolicySavesWithoutActorsAndRejectsLegacyActorConfiguration()
    {
        ApprovalPolicy saved = await Policies.SaveAsync(Draft(), default);
        Assert.Equal(1, saved.Version);
        string policyJson = await File.ReadAllTextAsync(Path.Combine(directory, "policies.json"));
        Assert.DoesNotContain("actor", policyJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("demoRoleAssignments", policyJson);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SavePolicyRequest>(
            """{"id":"policy","clientId":"client","workflowCode":"Gate2","stages":[{"name":"finance","roles":["Finance"]}],"demoRoleAssignments":{"Finance":"finance"}}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await Assert.ThrowsAsync<MissingDemoIdentityException>(() =>
            Identities.ResolveAsync("client", null, ["Finance"], default));
    }

    [Fact]
    public async Task IdentityEditsDoNotVersionPoliciesOrMutateResolvedRunningSnapshots()
    {
        PolicyStore policies = Policies;
        DemoIdentityStore identities = Identities;
        await policies.SaveAsync(Draft(), default);
        await identities.SaveAsync(Assignment("finance-one"), default);
        List<ApprovalChainRequest> starts = [];
        Mock<DurableTaskClient> client = new("test");
        client.Setup(item => item.GetInstanceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrchestrationMetadata?)null);
        client.Setup(item => item.ScheduleNewOrchestrationInstanceAsync(
                It.IsAny<TaskName>(), It.IsAny<object?>(), It.IsAny<StartOrchestrationOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((TaskName _, object? input, StartOrchestrationOptions? options, CancellationToken _) =>
            {
                starts.Add(Assert.IsType<ApprovalChainRequest>(input));
                return Task.FromResult(options!.InstanceId!);
            });
        ApprovalChainService service = new(client.Object, policies, identities);
        StartChainRequest request = new()
        {
            InstanceId = "run-one",
            ClientId = "client",
            WorkflowCode = "Gate2",
            PlanId = "sunny-soda",
            Version = 3,
            Requester = "requester"
        };
        await service.StartAsync(request, default);
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(directory, "policies.json"));
        await identities.SaveAsync(Assignment("finance-two"), default);
        await service.StartAsync(request with { InstanceId = "run-two" }, default);

        Assert.Equal(1, starts[0].Policy.Version);
        Assert.Equal(1, starts[1].Policy.Version);
        Assert.Equal("finance-one", starts[0].Policy.Stages[0].Approvals[0].Approver);
        Assert.Equal("finance-two", starts[1].Policy.Stages[0].Approvals[0].Approver);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(directory, "policies.json")));
        Assert.Equal("finance-two", (await Identities.ResolveAsync("client", null, ["Finance"], default))["Finance"]);
    }

    [Fact]
    public async Task MissingAssignmentsPreventStartBeforeScheduling()
    {
        PolicyStore policies = Policies;
        await policies.SaveAsync(Draft(), default);
        Mock<DurableTaskClient> client = new("test");
        ApprovalChainService service = new(client.Object, policies, Identities);
        await Assert.ThrowsAsync<MissingDemoIdentityException>(() => service.StartAsync(new()
        {
            InstanceId = "missing",
            ClientId = "client",
            WorkflowCode = "Gate2",
            PlanId = "sunny-soda",
            Version = 3,
            Requester = "requester"
        }, default));
        client.Verify(item => item.ScheduleNewOrchestrationInstanceAsync(
            It.IsAny<TaskName>(), It.IsAny<object?>(), It.IsAny<StartOrchestrationOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RoleAssignmentScopeIsAccountThenClientThenDemoDefault()
    {
        DemoIdentityStore identities = Identities;
        await identities.SaveAsync(Assignment("default-user") with { ClientId = "agency-default" }, default);
        await identities.SaveAsync(Assignment("client-user"), default);
        await identities.SaveAsync(Assignment("account-user") with { AccountId = "account" }, default);
        Assert.Equal("account-user", (await identities.ResolveAsync("client", "account", ["Finance"], default))["Finance"]);
        Assert.Equal("client-user", (await identities.ResolveAsync("client", "other", ["Finance"], default))["Finance"]);
        Assert.Equal("default-user", (await identities.ResolveAsync("other", null, ["Finance"], default))["Finance"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task EmptyActorsAreExplicitErrors(string actor)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Identities.SaveAsync(Assignment(actor), default));
    }

    private static SavePolicyRequest Draft() => new()
    {
        Id = "policy",
        ClientId = "client",
        WorkflowCode = "Gate2",
        Stages = [new("finance", ["Finance"])]
    };

    private static SaveDemoIdentitiesRequest Assignment(string actor) => new()
    {
        ClientId = "client",
        Assignments = new() { ["Finance"] = actor }
    };

    public void Dispose()
    {
        foreach (string name in new[] { "policies.json", "identities.json" })
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path)) File.Delete(path);
        }
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
}
