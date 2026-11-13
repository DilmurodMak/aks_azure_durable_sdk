using Approval.Contracts;
using Approval.Worker;
using Microsoft.DurableTask;
using Moq;

namespace Approval.Tests;

public sealed class ApprovalChainTests
{
    private static ApprovalChainRequest Request => new("sunny-soda", 3, "requester",
        new ResolvedApprovalPolicy("policy", "client", "account", "plan-approval", 7,
        [
            new("Review", 15, [new("finance", "assigned-finance"), new("legal", "assigned-legal")]),
            new("Release", 30, [new("owner", "assigned-owner")])
        ]), 12);

    [Fact]
    public async Task RolesRunInParallelButStagesAreOrderedAndReceiptWaitsForEveryApproval()
    {
        Harness harness = new();
        Task<ApprovalChainResult> workflow = harness.Start(Request);
        Assert.Equal(2, harness.Children.Count);
        Assert.All(harness.Children, child => Assert.Equal("Review", child.Request.Step!.Stage));
        Assert.Equal(new ApprovalProgress("Running", "Review", 1, 2, 0, 3), harness.Statuses[0]);
        harness.AssertNoApply();

        harness.Children[1].Complete("Approved");
        await harness.WaitForProgress(1);
        Assert.False(workflow.IsCompleted);
        Assert.Equal(2, harness.Children.Count);
        Assert.Equal(new ApprovalProgress("Running", "Review", 1, 2, 1, 3), harness.Statuses[^1]);
        harness.AssertNoApply();

        harness.Children[0].Complete("Approved");
        await harness.WaitForChildren(3);
        Assert.Equal(new ApprovalProgress("Running", "Release", 2, 2, 2, 3), harness.Statuses[^1]);
        Assert.Equal(2, harness.Statuses.Count(status => status.Stage == "Review" && status.Completed == 2));
        Assert.False(workflow.IsCompleted);
        harness.AssertNoApply();
        harness.Children[2].Complete("Approved");
        ApprovalChainResult result = await workflow;

        Assert.Equal("Approved", result.Status);
        Assert.Equal(3, result.Version);
        Assert.Equal("policy", result.PolicyId);
        Assert.Equal(7, result.PolicyVersion);
        Assert.Equal("sunny-soda:v3:approved", result.Receipt);
        Assert.Equal(["finance", "legal", "owner"], result.Steps.Select(step => step.Role));
        Assert.Equal(["assigned-finance", "assigned-legal", "assigned-owner"],
            result.Steps.Select(step => step.Approver));
        Assert.All(result.Steps, step => Assert.Equal("Approved", step.Status));
        Assert.Equal(new ApprovalProgress("Approved", "Release", 2, 2, 3, 3), harness.Statuses[^1]);
        harness.Context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), new ApplyDecisionRequest("sunny-soda:v3:approved", 12),
            It.IsAny<TaskOptions?>()), Times.Once);
        harness.Context.Verify(item => item.CallActivityAsync<string>(
            It.IsAny<TaskName>(), It.IsAny<object?>(), It.IsAny<TaskOptions?>()), Times.Once);
    }

    [Theory]
    [InlineData("Rejected", "Approved", "Approved", "Rejected")]
    [InlineData("Timeout", "Approved", "Approved", "Timeout")]
    [InlineData("Timeout", "Rejected", "Timeout", "Rejected")]
    [InlineData("Rejected", "Timeout", "Rejected", "Rejected")]
    [InlineData("Approved", "Approved", "Timeout", "Timeout")]
    public async Task NegativeOutcomesWaitForAllRolesAndContinueRemainingStages(
        string first, string second, string last, string expected)
    {
        Harness harness = new();
        Task<ApprovalChainResult> workflow = harness.Start(Request);
        harness.Children[0].Complete(first);
        await harness.WaitForProgress(1);
        Assert.False(workflow.IsCompleted);
        Assert.Equal(2, harness.Children.Count);
        harness.AssertNoApply();

        harness.Children[1].Complete(second);
        await harness.WaitForChildren(3);
        Assert.False(workflow.IsCompleted);
        harness.AssertNoApply();
        harness.Children[2].Complete(last);
        ApprovalChainResult result = await workflow;

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Receipt);
        Assert.Equal([first, second, last], result.Steps.Select(step => step.Status));
        Assert.All(harness.Children, child => Assert.True(child.Completion.Task.IsCompletedSuccessfully));
        Assert.Equal(harness.Children.Count, harness.Statuses[^1].Completed);
        Assert.Equal(harness.Statuses[^1].Total, harness.Statuses[^1].Completed);
        Assert.Equal(new ApprovalProgress(expected, "Release", 2, 2, 3, 3), harness.Statuses[^1]);
        harness.AssertNoApply();
    }

    [Fact]
    public async Task ChildrenUseOnlyTheSubmittedSnapshotAndSharedTagsWithDeterministicIds()
    {
        Harness harness = new(autoComplete: true);
        ApprovalChainRequest request = Request;
        ApprovalChainResult result = await harness.Start(request);

        for (int index = 0; index < harness.Children.Count; index++)
        {
            Child child = harness.Children[index];
            bool last = index == 2;
            string stage = last ? "Release" : "Review";
            string role = last ? "owner" : index == 0 ? "finance" : "legal";
            int stageNumber = last ? 2 : 1;
            int roleNumber = last ? 1 : index + 1;
            Assert.Equal($"chain-001-s{stageNumber}-r{roleNumber}", child.Options.InstanceId);
            Assert.Equal(result.Steps[index].InstanceId, child.Options.InstanceId);
            Assert.Null(child.Options.Version);
            Assert.Equal(new ApprovalStepContext("chain-001", "client", "account", "plan-approval",
                "policy", 7, stage, stageNumber, 2, role), child.Request.Step);
            Assert.Equal(last ? 30 : 15, child.Request.TimeoutMinutes);
            Assert.Equal(request.PlanId, child.Request.PlanId);
            Assert.Equal(request.Version, child.Request.Version);
            Assert.Equal(request.Requester, child.Request.Requester);
            Assert.Equal(request.ApplyDecisionDelaySeconds, child.Request.ApplyDecisionDelaySeconds);
            Dictionary<string, string> expectedTags = ApprovalTags.Create("client", "plan-approval",
                "sunny-soda", 3, "policy-chain", stage, "policy", 7, "chain-001");
            Assert.Equal(expectedTags.OrderBy(tag => tag.Key), child.Options.Tags!.OrderBy(tag => tag.Key));
        }

        Assert.All(harness.Context.Invocations.Where(invocation => invocation.Method.Name == "CallActivityAsync"),
            invocation => Assert.Equal((TaskName)nameof(ApplyDecision), invocation.Arguments[0]));
        Harness replay = new(autoComplete: true);
        await replay.Start(request);
        Assert.Equal(harness.Children.Select(child => child.Options.InstanceId),
            replay.Children.Select(child => child.Options.InstanceId));
    }

    [Fact]
    public async Task MaximumSnapshotHasUniqueShortChildIdsAndAllowsRolesToRepeatAcrossStages()
    {
        ApprovalChainRequest request = Request;
        request = request with
        {
            Policy = request.Policy with
            {
                AccountId = null,
                Stages = Enumerable.Range(1, 10).Select(stage => new ResolvedPolicyStage($"stage-{stage}", 1440,
                    Enumerable.Range(1, 10).Select(role => new ResolvedRole($"role-{role}", "assigned")).ToArray()))
                    .ToArray()
            }
        };
        Harness harness = new(new string('p', 80), autoComplete: true);

        ApprovalChainResult result = await harness.Start(request);

        Assert.Equal(100, result.Steps.Length);
        Assert.Equal(100, result.Steps.Select(step => step.InstanceId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(result.Steps, step => Assert.InRange(step.InstanceId.Length, 1, 128));
        Assert.Equal($"{new string('p', 80)}-s10-r10", result.Steps[^1].InstanceId);
        Assert.Equal(new ApprovalProgress("Approved", "stage-10", 10, 10, 100, 100), harness.Statuses[^1]);
    }

    [Fact]
    public async Task AChildFaultIsPropagatedOnlyAfterAllScheduledSiblingsSettle()
    {
        Harness harness = new();
        Task<ApprovalChainResult> workflow = harness.Start(Request);
        harness.Children[0].Completion.SetException(new InvalidOperationException("Child failed"));
        await harness.WaitForProgress(1);
        Assert.False(workflow.IsCompleted);
        harness.AssertNoApply();
        harness.Children[1].Complete("Approved");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow);

        Assert.Equal("Child failed", exception.Message);
        Assert.Equal(2, harness.Children.Count);
        harness.AssertNoApply();
        Assert.DoesNotContain(harness.Statuses, status => status.State == "Approved");
    }

    [Fact]
    public async Task ParentDoesNotPublishApprovedUntilTheSingleApplyActivitySucceeds()
    {
        Harness harness = new(autoComplete: true);
        TaskCompletionSource<string> apply = new();
        harness.Context.Setup(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>())).Returns(apply.Task);

        Task<ApprovalChainResult> workflow = harness.Start(Request);

        Assert.Equal(3, harness.Children.Count);
        Assert.False(workflow.IsCompleted);
        Assert.Equal(new ApprovalProgress("Running", "Release", 2, 2, 3, 3), harness.Statuses[^1]);
        apply.SetException(new InvalidOperationException("Apply failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow);
        Assert.DoesNotContain(harness.Statuses, status => status.State == "Approved");
    }

    [Theory]
    [InlineData("null-request")]
    [InlineData("null-policy")]
    [InlineData("policy-id")]
    [InlineData("client")]
    [InlineData("account")]
    [InlineData("workflow")]
    [InlineData("policy-version")]
    [InlineData("null-stages")]
    [InlineData("empty-stages")]
    [InlineData("many-stages")]
    [InlineData("null-stage")]
    [InlineData("stage-name")]
    [InlineData("duplicate-stages")]
    [InlineData("null-roles")]
    [InlineData("empty-roles")]
    [InlineData("many-roles")]
    [InlineData("duplicate-roles")]
    [InlineData("null-role")]
    [InlineData("role-name")]
    [InlineData("null-approver")]
    [InlineData("empty-approver")]
    [InlineData("timeout-zero")]
    [InlineData("timeout-large")]
    [InlineData("timeout-nan")]
    [InlineData("timeout-infinity")]
    [InlineData("plan")]
    [InlineData("business-version")]
    [InlineData("requester")]
    [InlineData("delay-negative")]
    [InlineData("delay-large")]
    [InlineData("late-invalid-stage")]
    public async Task UntrustedSnapshotsAreValidatedBeforeAnySideEffects(string invalid)
    {
        ApprovalChainRequest request = Request;
        ResolvedApprovalPolicy policy = request.Policy;
        ResolvedPolicyStage stage = policy.Stages[0];
        request = invalid switch
        {
            "null-request" => null!,
            "null-policy" => request with { Policy = null! },
            "plan" => request with { PlanId = "bad/plan" },
            "business-version" => request with { Version = 0 },
            "requester" => request with { Requester = " " },
            "delay-negative" => request with { ApplyDecisionDelaySeconds = -1 },
            "delay-large" => request with { ApplyDecisionDelaySeconds = 61 },
            _ => request with
            {
                Policy = invalid switch
                {
                    "policy-id" => policy with { Id = new string('p', 81) },
                    "client" => policy with { ClientId = "" },
                    "account" => policy with { AccountId = "bad/account" },
                    "workflow" => policy with { WorkflowCode = "bad workflow" },
                    "policy-version" => policy with { Version = 0 },
                    "null-stages" => policy with { Stages = null! },
                    "empty-stages" => policy with { Stages = [] },
                    "many-stages" => policy with { Stages = Enumerable.Repeat(stage, 11).ToArray() },
                    "null-stage" => policy with { Stages = [null!] },
                    "stage-name" => policy with { Stages = [stage with { Name = "bad/stage" }] },
                    "duplicate-stages" => policy with { Stages = [stage, stage with { Name = "REVIEW" }] },
                    "late-invalid-stage" => policy with { Stages = [stage, stage with { Name = "bad/stage" }] },
                    _ => policy with
                    {
                        Stages = [invalid switch
                        {
                            "null-roles" => stage with { Approvals = null! },
                            "empty-roles" => stage with { Approvals = [] },
                            "many-roles" => stage with { Approvals = Enumerable.Repeat(stage.Approvals[0], 11).ToArray() },
                            "duplicate-roles" => stage with { Approvals = [stage.Approvals[0], new("FINANCE", "another")] },
                            "null-role" => stage with { Approvals = [null!] },
                            "role-name" => stage with { Approvals = [new("bad/role", "assigned")] },
                            "null-approver" => stage with { Approvals = [new("finance", null!)] },
                            "empty-approver" => stage with { Approvals = [new("finance", " ")] },
                            "timeout-zero" => stage with { TimeoutMinutes = 0 },
                            "timeout-large" => stage with { TimeoutMinutes = 1441 },
                            "timeout-nan" => stage with { TimeoutMinutes = double.NaN },
                            "timeout-infinity" => stage with { TimeoutMinutes = double.PositiveInfinity },
                            _ => throw new InvalidOperationException(invalid)
                        }]
                    }
                }
            }
        };
        Harness harness = new();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => harness.Start(request));

        Assert.Empty(harness.Children);
        Assert.Empty(harness.Statuses);
        harness.AssertNoApply();
    }

    [Fact]
    public async Task OversizedParentIdIsRejectedBeforeSchedulingChildren()
    {
        Harness harness = new(new string('p', 81));

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Start(Request));

        Assert.Empty(harness.Children);
        Assert.Empty(harness.Statuses);
        harness.AssertNoApply();
    }

    private sealed class Child(ApprovalRequest request, SubOrchestrationOptions options)
    {
        public ApprovalRequest Request { get; } = request;
        public SubOrchestrationOptions Options { get; } = options;
        public TaskCompletionSource<ApprovalResult> Completion { get; } = new();

        public void Complete(string status) => Completion.SetResult(new(status, Request.Version, null));
    }

    private sealed class Harness
    {
        private TaskCompletionSource changed = new();
        public Mock<TaskOrchestrationContext> Context { get; } = new(MockBehavior.Strict);
        public List<Child> Children { get; } = [];
        public List<ApprovalProgress> Statuses { get; } = [];

        public Harness(string instanceId = "chain-001", bool autoComplete = false)
        {
            Context.SetupGet(item => item.InstanceId).Returns(instanceId);
            Context.Setup(item => item.SetCustomStatus(It.IsAny<object?>())).Callback<object?>(status =>
            {
                Statuses.Add(Assert.IsType<ApprovalProgress>(status));
                Notify();
            });
            Context.Setup(item => item.CallSubOrchestratorAsync<ApprovalResult>(
                ApprovalNames.StepOrchestration, It.IsAny<object?>(), It.IsAny<TaskOptions?>()))
                .Returns<TaskName, object?, TaskOptions?>((_, request, options) =>
                {
                    Child child = new(Assert.IsType<ApprovalRequest>(request),
                        Assert.IsType<SubOrchestrationOptions>(options));
                    Children.Add(child);
                    if (autoComplete) child.Complete("Approved");
                    Notify();
                    return child.Completion.Task;
                });
            Context.Setup(item => item.CallActivityAsync<string>(
                nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()))
                .ReturnsAsync("sunny-soda:v3:approved");
        }

        public Task<ApprovalChainResult> Start(ApprovalChainRequest request) =>
            new ApprovalChain().RunAsync(Context.Object, request);

        public void AssertNoApply() => Context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()), Times.Never);

        public async Task WaitForChildren(int count)
        {
            while (Children.Count < count) await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task WaitForProgress(int completed)
        {
            while (Statuses.Count == 0 || Statuses[^1].Completed < completed)
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private void Notify()
        {
            TaskCompletionSource previous = changed;
            changed = new();
            previous.TrySetResult();
        }
    }
}
