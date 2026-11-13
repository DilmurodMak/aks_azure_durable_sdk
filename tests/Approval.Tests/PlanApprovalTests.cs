using Approval.Contracts;
using Approval.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Approval.Tests;

public sealed class PlanApprovalTests
{
    private static readonly ApprovalRequest Request = new("sunny-soda", 3, "requester", "finance", 15);

    [Theory]
    [InlineData("finance", "Approve", 3, "Approved")]
    [InlineData("finance", "Reject", 3, "Rejected")]
    [InlineData("finance", "approve", 3, "Rejected")]
    [InlineData("finance", "", 3, "Rejected")]
    [InlineData("finance", "anything", 3, "Rejected")]
    [InlineData("requester", "Approve", 3, "RejectedSelfApproval")]
    [InlineData("REQUESTER", "Approve", 3, "RejectedSelfApproval")]
    [InlineData("another-person", "Approve", 3, "RejectedWrongApprover")]
    [InlineData("finance", "Approve", 2, "RejectedStaleVersion")]
    [InlineData("finance", "Approve", 4, "RejectedStaleVersion")]
    public void OnlyAnExplicitValidDecisionApproves(string approver, string outcome, int version, string expected)
    {
        Assert.Equal(expected, PlanApproval.EvaluateDecision(Request, new Decision(approver, outcome, version)));
    }

    [Fact]
    public void NullDecisionRejects() => Assert.Equal("Rejected", PlanApproval.EvaluateDecision(Request, null));

    [Theory]
    [InlineData("Approve", 3, "Approved", "sunny-soda:v3:approved")]
    [InlineData("Approve", 2, "RejectedStaleVersion", null)]
    [InlineData("Reject", 3, "Rejected", null)]
    public async Task DecisionPathCancelsTimerAndOnlyApprovalCallsApplyDecision(
        string outcome, int version, string expected, string? receipt)
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        CancellationToken timerToken = default;
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime, CancellationToken>((_, token) => timerToken = token)
            .Returns(new TaskCompletionSource().Task);
        context.Setup(item => item.WaitForExternalEvent<Decision>(ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Decision("finance", outcome, version));

        ApprovalResult result = await new PlanApproval().RunAsync(context.Object, Request);

        Assert.Equal(new ApprovalResult(expected, 3, receipt), result);
        Assert.True(timerToken.IsCancellationRequested);
        context.Verify(item => item.CreateTimer(
            new DateTime(2026, 11, 13, 12, 15, 0, DateTimeKind.Utc), It.IsAny<CancellationToken>()), Times.Once);
        context.Verify(item => item.SetCustomStatus(ApprovalNames.WaitingStatus), Times.Once);
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()),
            receipt is null ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task TimeoutCancelsDecisionAndNeverAppliesAReceipt()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        CancellationToken eventToken = default;
        context.Setup(item => item.WaitForExternalEvent<Decision>(ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((_, token) => eventToken = token)
            .Returns(new TaskCompletionSource<Decision>().Task);
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        ApprovalResult result = await new PlanApproval().RunAsync(context.Object, Request);

        Assert.Equal(new ApprovalResult("Timeout", 3, null), result);
        Assert.True(eventToken.IsCancellationRequested);
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()), Times.Never);
    }

    [Fact]
    public async Task ActivityFailureIsNotReportedAsAnApproval()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        context.Setup(item => item.CallActivityAsync<string>(
            nameof(RecordSubmission), It.IsAny<object?>(), It.IsAny<TaskOptions?>()))
            .ThrowsAsync(new InvalidOperationException("Submission failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PlanApproval().RunAsync(context.Object, Request));
        context.Verify(item => item.SetCustomStatus(ApprovalNames.WaitingStatus), Times.Never);
    }

    [Fact]
    public async Task TimerFailureIsNotReportedAsATimeout()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        context.Setup(item => item.WaitForExternalEvent<Decision>(ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<Decision>().Task);
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromException(new InvalidOperationException("Timer failed")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PlanApproval().RunAsync(context.Object, Request));

        context.Verify(item => item.SetCustomStatus("Timeout"), Times.Never);
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()), Times.Never);
    }

    private static Mock<TaskOrchestrationContext> CreateContext()
    {
        Mock<TaskOrchestrationContext> context = new();
        context.SetupGet(item => item.InstanceId).Returns("sunny-soda-v3-001");
        context.SetupGet(item => item.CurrentUtcDateTime)
            .Returns(new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc));
        context.Setup(item => item.CallActivityAsync<string>(
            nameof(RecordSubmission), It.IsAny<object?>(), It.IsAny<TaskOptions?>())).ReturnsAsync("sunny-soda");
        context.Setup(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), "sunny-soda:v3:approved", It.IsAny<TaskOptions?>()))
            .ReturnsAsync("sunny-soda:v3:approved");
        context.Setup(item => item.CreateReplaySafeLogger<PlanApproval>()).Returns(NullLogger.Instance);
        return context;
    }
}
