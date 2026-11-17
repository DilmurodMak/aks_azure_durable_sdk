using Approval.Client;
using Approval.Console;
using Approval.Contracts;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Converters;
using Moq;

namespace Approval.Tests;

public sealed class WorkflowApprovalProjectionTests
{
    private static readonly DataConverter Converter = new JsonDataConverter(ApprovalJson.CreateSerializerOptions());
    private static ApprovalChainRequest Input => new("sunny-soda", 3, "requester", "corr-001",
        new("policy", "client", null, "B07", 7,
        [
            new("parallel-review", 15, [new("Finance", "finance"), new("Legal", "legal")]),
            new("sign-off", 15, [new("Client", "client")])
        ], "client default"), new BudgetRouting(7500, []));

    [Fact]
    public async Task ParallelReviewersAreActionableButFutureStageIsShownBlocked()
    {
        var (client, instances) = Client();
        instances["run-s1-r1"] = Child(1, 1, OrchestrationRuntimeStatus.Running);
        instances["run-s1-r2"] = Child(1, 2, OrchestrationRuntimeStatus.Running);
        Mock<IPolicyStore> policies = new();
        Mock<IDemoIdentityStore> identities = new();
        var rows = await new ApprovalChainService(client.Object, policies.Object, identities.Object).StepsAsync("run", default);

        Assert.Equal(["1.1", "1.2", "2.1"], rows.Select(item => item.Rank));
        Assert.All(rows.Take(2), row => Assert.True(row.CanDecide));
        Assert.False(rows[2].CanDecide);
        Assert.False(rows[2].CanView);
        Assert.Equal("Blocked", rows[2].State);
        Assert.Contains("stage 1", rows[2].BlockedReason);
        Assert.Equal("sign-off", rows[2].Progress.Stage);
        Assert.All(rows, row => Assert.Equal(7, row.PolicyVersion));
        policies.VerifyNoOtherCalls();
        identities.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BeforeTheWorkerStartsOnlyTheFirstGroupIsQueued()
    {
        var (client, _) = Client(OrchestrationRuntimeStatus.Pending);
        var rows = await Service(client).StepsAsync("run", default);
        Assert.All(rows.Take(2), row => Assert.Equal("Queued", row.State));
        Assert.Equal("Blocked", rows[2].State);
        Assert.All(rows, row => Assert.False(row.CanDecide || row.CanView));
    }

    [Fact]
    public async Task FinishedWorkflowRetainsEveryStepForHistoryButNoDecisions()
    {
        var (client, instances) = Client(OrchestrationRuntimeStatus.Completed, stage: 2);
        instances["run-s1-r1"] = Child(1, 1, OrchestrationRuntimeStatus.Completed);
        instances["run-s1-r2"] = Child(1, 2, OrchestrationRuntimeStatus.Completed);
        instances["run-s2-r1"] = Child(2, 1, OrchestrationRuntimeStatus.Completed);
        var rows = await Service(client).StepsAsync("run", default);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("Approved", row.State);
            Assert.True(row.CanView);
            Assert.False(row.CanDecide);
        });
    }

    [Fact]
    public async Task OneParallelCompletionDoesNotUnlockTheNextGroup()
    {
        var (client, instances) = Client();
        instances["run-s1-r1"] = Child(1, 1, OrchestrationRuntimeStatus.Completed);
        instances["run-s1-r2"] = Child(1, 2, OrchestrationRuntimeStatus.Running);
        var rows = await Service(client).StepsAsync("run", default);

        Assert.Equal("Approved", rows[0].State);
        Assert.True(rows[0].CanView);
        Assert.False(rows[0].CanDecide);
        Assert.True(rows[1].CanDecide);
        Assert.False(rows[2].CanDecide);
        Assert.Equal("Blocked", rows[2].State);
    }

    [Fact]
    public async Task CompletedGroupStaysVisibleAndNextGroupUnlocks()
    {
        var (client, instances) = Client(stage: 2);
        instances["run-s1-r1"] = Child(1, 1, OrchestrationRuntimeStatus.Completed);
        instances["run-s1-r2"] = Child(1, 2, OrchestrationRuntimeStatus.Completed, "Rejected");
        instances["run-s2-r1"] = Child(2, 1, OrchestrationRuntimeStatus.Running);
        var rows = await Service(client).StepsAsync("run", default);

        Assert.Equal(3, rows.Count);
        Assert.Equal("Approved", rows[0].State);
        Assert.Equal("Rejected", rows[1].State);
        Assert.All(rows.Take(2), row => Assert.False(row.CanDecide));
        Assert.True(rows[2].CanDecide);
        Assert.Equal("Waiting", rows[2].State);
    }

    [Theory]
    [InlineData(OrchestrationRuntimeStatus.Suspended)]
    [InlineData(OrchestrationRuntimeStatus.Terminated)]
    public async Task NonRunningParentNeverOffersDecisions(OrchestrationRuntimeStatus status)
    {
        var (client, instances) = Client(status);
        instances["run-s1-r1"] = Child(1, 1, OrchestrationRuntimeStatus.Running);
        var rows = await Service(client).StepsAsync("run", default);
        Assert.All(rows, row => Assert.False(row.CanDecide));
        Assert.All(rows, row => Assert.Contains(status.ToString(), row.BlockedReason));
    }

    [Fact]
    public async Task FutureStepCannotBeSignaledBeforeItExists()
    {
        var (client, _) = Client();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ClientOperations.ExecuteAsync(client.Object,
            new("decide", "run-s2-r1", new("client", "Approve", 3)), default));
        client.Verify(item => item.RaiseEventAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EvenAnExistingFutureStepIsRejectedByTheParentStageGuard()
    {
        var (client, instances) = Client();
        instances["run-s2-r1"] = Child(2, 1, OrchestrationRuntimeStatus.Running);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClientOperations.ExecuteAsync(client.Object,
            new("decide", "run-s2-r1", new("client", "Approve", 3)), default));
        client.Verify(item => item.RaiseEventAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ActiveStepCanBeSignaled()
    {
        var (client, instances) = Client();
        instances["run-s1-r2"] = Child(1, 2, OrchestrationRuntimeStatus.Running);
        client.Setup(item => item.RaiseEventAsync(
            "run-s1-r2", "Decision", It.IsAny<object?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await ClientOperations.ExecuteAsync(client.Object,
            new("decide", "run-s1-r2", new("legal", "Approve", 3)), default);
        client.Verify(item => item.RaiseEventAsync(
            "run-s1-r2", "Decision", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static readonly DateTimeOffset DecidedAt = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ARejectedChainShowsWhoDecidedWhyAndNeverStartedTheLaterStage()
    {
        var (client, instances) = Client(OrchestrationRuntimeStatus.Completed);
        instances["run-s1-r1"] = Child(1, 1, OrchestrationRuntimeStatus.Completed, "Rejected");
        instances["run-s1-r2"] = Child(1, 2, OrchestrationRuntimeStatus.Completed, "Canceled");
        var rows = await Service(client).StepsAsync("run", default);

        Assert.Equal(["Rejected", "Canceled", "NotStarted"], rows.Select(row => row.State));
        Assert.Equal("finance", rows[0].DecidedBy);
        Assert.Equal(DecidedAt, rows[0].DecidedAt);
        Assert.Equal("Needs work", rows[0].Comment);
        Assert.Null(rows[1].DecidedBy);
        Assert.Null(rows[2].DecidedAt);
        Assert.False(rows[2].CanView);
        Assert.All(rows, row => Assert.False(row.CanDecide));
    }

    [Fact]
    public async Task ARejectionOrReturnWithoutACommentIsRefusedBeforeAnythingIsSignaled()
    {
        var (client, instances) = Client();
        instances["run-s1-r1"] = Child(1, 1, OrchestrationRuntimeStatus.Running);

        foreach (string outcome in new[] { "Reject", "Return" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => ClientOperations.ExecuteAsync(client.Object,
                new("decide", "run-s1-r1", new("finance", outcome, 3, "  ")), default));
        }

        client.Verify(item => item.RaiseEventAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ADecisionWithACommentIsRaisedAsTheDecisionEvent()
    {
        var (client, instances) = Client();
        instances["run-s1-r1"] = Child(1, 1, OrchestrationRuntimeStatus.Running);
        Decision sent = new("finance", "Return", 3, "Add the budget");
        client.Setup(item => item.RaiseEventAsync(
            "run-s1-r1", "Decision", sent, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await ClientOperations.ExecuteAsync(client.Object, new("decide", "run-s1-r1", sent), default);

        client.Verify(item => item.RaiseEventAsync(
            "run-s1-r1", "Decision", sent, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ApprovalChainService Service(Mock<DurableTaskClient> client) =>
        new(client.Object, Mock.Of<IPolicyStore>(), Mock.Of<IDemoIdentityStore>());

    private static (Mock<DurableTaskClient>, Dictionary<string, OrchestrationMetadata>) Client(
        OrchestrationRuntimeStatus parentStatus = OrchestrationRuntimeStatus.Running, int stage = 1)
    {
        Dictionary<string, OrchestrationMetadata> instances = new()
        {
            ["run"] = new(ApprovalNames.ChainOrchestration, "run")
            {
                RuntimeStatus = parentStatus,
                DataConverter = Converter,
                SerializedInput = Converter.Serialize(Input),
                SerializedCustomStatus = Converter.Serialize(new ApprovalProgress("Running", "review", stage, 2))
            }
        };
        Mock<DurableTaskClient> client = new("test");
        client.Setup(item => item.GetInstanceAsync(
                It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .Returns((string id, bool _, CancellationToken _) => Task.FromResult(instances.GetValueOrDefault(id)));
        return (client, instances);
    }

    private static OrchestrationMetadata Child(int stage, int reviewer, OrchestrationRuntimeStatus status, string outcome = "Approved")
    {
        ResolvedPolicyStage stagePolicy = Input.Policy.Stages[stage - 1];
        ResolvedRole role = stagePolicy.Approvals[reviewer - 1];
        ApprovalRequest input = new("sunny-soda", 3, "requester", role.Approver, 15, Step:
            new("run", "client", null, "B07", "policy", 7, stagePolicy.Name, stage, 2, role.Role, "corr-001"));
        return new(ApprovalNames.StepOrchestration, ApprovalNames.StepInstanceId("run", stage, reviewer))
        {
            RuntimeStatus = status,
            DataConverter = Converter,
            SerializedInput = Converter.Serialize(input),
            SerializedCustomStatus = Converter.Serialize(new ApprovalProgress("Waiting", stagePolicy.Name, stage, 2)),
            SerializedOutput = status == OrchestrationRuntimeStatus.Completed
                ? Converter.Serialize(new ApprovalResult(outcome, 3,
                    outcome is "Timeout" or "Canceled" ? null : role.Approver, DecidedAt,
                    outcome is "Rejected" or "Returned" ? "Needs work" : null)) : null
        };
    }
}
