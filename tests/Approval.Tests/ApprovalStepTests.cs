using System.Text.Json;
using Approval.Contracts;
using Approval.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Converters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Approval.Tests;

public sealed class ApprovalStepTests
{
    private static readonly DateTime Start = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ApprovalRequest Request = new("sunny-soda", 3, "requester", "finance", 15,
        new ApprovalStepContext("chain-001", "client", null, "B07", "policy", 1, "finance", 1, 1, "Finance", "corr-001"));
    private static readonly DateTimeOffset StartOffset = new(Start);
    private static ApprovalResult Approved => new("Approved", 3, "finance", StartOffset);
    private static ApprovalResult TimedOut => new("Timeout", 3, DecidedAt: StartOffset);
    private static readonly ApprovalProgress Waiting = new("Waiting", "finance", 1, 1);

    [Theory]
    [InlineData("another-person", "Approve", 3)]
    [InlineData("requester", "Approve", 3)]
    [InlineData("REQUESTER", "Approve", 3)]
    [InlineData("finance", "Approve", 2)]
    [InlineData("finance", "Approve", 4)]
    [InlineData("", "Approve", 3)]
    [InlineData(null, "Approve", 3)]
    [InlineData("finance", "", 3)]
    [InlineData("finance", " ", 3)]
    [InlineData("finance", null, 3)]
    [InlineData("finance", "approve", 3)]
    [InlineData("finance", "reject", 3)]
    [InlineData("finance", "Aprove", 3)]
    public async Task RefusedDecisionKeepsWaitingUntilAnExactApproval(
        string? approver, string? outcome, int version)
    {
        Mock<ILogger> logger = new();
        Mock<TaskOrchestrationContext> context = CreateContext(logger.Object);
        TaskCompletionSource<Decision> validDecision = new();
        CancellationToken timerToken = default;
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime, CancellationToken>((_, token) => timerToken = token)
            .Returns<DateTime, CancellationToken>((_, token) => CancelableTimer(token));
        context.SetupSequence(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Decision(approver!, outcome!, version))
            .Returns(validDecision.Task);

        Task<ApprovalResult> workflow = new ApprovalStep().RunAsync(context.Object, Request);

        Assert.False(workflow.IsCompleted);
        Assert.False(timerToken.IsCancellationRequested);
        AssertWaitingWithoutApply(context);
        context.Verify(item => item.CreateReplaySafeLogger<ApprovalStep>(), Times.Once);
        VerifyLog(logger, LogLevel.Warning, "refused decision", Times.Once());
        VerifyLog(logger, LogLevel.Warning, Waiting.Display, Times.Once());

        validDecision.SetResult(new Decision("FINANCE", "Approve", 3));
        ApprovalResult result = await workflow;

        Assert.Equal(Approved, result);
        Assert.True(timerToken.IsCancellationRequested);
        context.Verify(item => item.CreateTimer(Start.AddMinutes(15), It.IsAny<CancellationToken>()), Times.Once);
        context.Verify(item => item.SetCustomStatus(Waiting), Times.Once);
        context.Verify(item => item.SetCustomStatus(new ApprovalProgress("Approved", "finance", 1, 1, 1)), Times.Once);
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()), Times.Never);
    }

    [Fact]
    public async Task NullDecisionIsRefusedUntilAnExactRejection()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        CancellationToken timerToken = default;
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime, CancellationToken>((_, token) => timerToken = token)
            .Returns<DateTime, CancellationToken>((_, token) => CancelableTimer(token));
        context.SetupSequence(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Decision)null!)
            .ReturnsAsync(new Decision("finance", "Reject", 3, " Budget is wrong "));

        ApprovalResult result = await new ApprovalStep().RunAsync(context.Object, Request);

        Assert.Equal(new ApprovalResult("Rejected", 3, "finance", StartOffset, "Budget is wrong"), result);
        Assert.True(timerToken.IsCancellationRequested);
        context.Verify(item => item.WaitForExternalEvent<Decision>(
            ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()), Times.Exactly(2));
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()), Times.Never);
    }

    [Fact]
    public async Task MultipleRefusalsUseOneOriginalDeadlineAndThenTimeoutWithoutApplying()
    {
        Mock<ILogger> logger = new();
        Mock<TaskOrchestrationContext> context = CreateContext(logger.Object);
        TaskCompletionSource timer = new();
        CancellationToken eventToken = default;
        int waits = 0;
        List<DateTime> deadlines = [];
        List<ApprovalProgress?> statuses = [];
        context.Setup(item => item.SetCustomStatus(It.IsAny<object?>()))
            .Callback<object?>(status => statuses.Add(status as ApprovalProgress));
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime, CancellationToken>((deadline, _) => deadlines.Add(deadline))
            .Returns(timer.Task);
        Queue<Decision> refused = new([
            new Decision("another-person", "Approve", 3),
            new Decision("requester", "Approve", 3),
            new Decision("finance", "Approve", 2),
            new Decision("finance", "typo", 3),
        ]);
        context.Setup(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((_, token) =>
            {
                eventToken = token;
                waits++;
                context.SetupGet(item => item.CurrentUtcDateTime).Returns(Start.AddMinutes(waits));
            })
            .Returns(() => refused.Count > 0
                ? Task.FromResult(refused.Dequeue())
                : new TaskCompletionSource<Decision>().Task);

        Task<ApprovalResult> workflow = new ApprovalStep().RunAsync(context.Object, Request);

        Assert.False(workflow.IsCompleted);
        Assert.Equal([Waiting], statuses);
        Assert.Equal([Start.AddMinutes(15)], deadlines);
        Assert.Equal(5, waits);
        Assert.False(eventToken.IsCancellationRequested);
        AssertWaitingWithoutApply(context);
        VerifyLog(logger, LogLevel.Warning, "refused decision", Times.Exactly(4));

        timer.SetResult();
        ApprovalResult result = await workflow;

        Assert.Equal(new ApprovalResult("Timeout", 3, DecidedAt: StartOffset.AddMinutes(5)), result);
        Assert.True(eventToken.IsCancellationRequested);
        Assert.Equal([Waiting, Waiting with { State = "Timeout", Completed = 1 }], statuses);
        context.Verify(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()), Times.Never);
    }

    [Fact]
    public async Task OriginalDeadlineWinsWhenATimerAndDecisionAreBothReady()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        context.Setup(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Decision("finance", "Approve", 3));

        ApprovalResult result = await new ApprovalStep().RunAsync(context.Object, Request);

        Assert.Equal(TimedOut, result);
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()), Times.Never);
    }

    [Fact]
    public async Task TimeoutWithoutAnyDecisionCancelsTheWaitAndDoesNotApply()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        CancellationToken eventToken = default;
        context.Setup(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((_, token) => eventToken = token)
            .Returns(new TaskCompletionSource<Decision>().Task);
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        ApprovalResult result = await new ApprovalStep().RunAsync(context.Object, Request);

        Assert.Equal(TimedOut, result);
        Assert.True(eventToken.IsCancellationRequested);
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimerFailurePropagatesEvenWithAReadyDecision(bool readyDecision)
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        context.Setup(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .Returns(readyDecision
                ? Task.FromResult(new Decision("finance", "Approve", 3))
                : new TaskCompletionSource<Decision>().Task);
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromException(new InvalidOperationException("Timer failed")));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ApprovalStep().RunAsync(context.Object, Request));

        Assert.Equal("Timer failed", error.Message);
        AssertWaitingWithoutApply(context);
    }

    [Fact]
    public async Task TimerFailureAfterARefusalStillPropagates()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        TaskCompletionSource timer = new();
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(timer.Task);
        context.SetupSequence(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Decision("finance", "typo", 3))
            .Returns(new TaskCompletionSource<Decision>().Task);

        Task<ApprovalResult> workflow = new ApprovalStep().RunAsync(context.Object, Request);
        Assert.False(workflow.IsCompleted);
        timer.SetException(new InvalidOperationException("Timer failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow);
        AssertWaitingWithoutApply(context);
    }

    [Fact]
    public async Task SubmissionFailureDoesNotBeginWaiting()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        context.Setup(item => item.CallActivityAsync<string>(
                nameof(RecordSubmission), It.IsAny<object?>(), It.IsAny<TaskOptions?>()))
            .ThrowsAsync(new InvalidOperationException("Submission failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ApprovalStep().RunAsync(context.Object, Request));

        context.Verify(item => item.SetCustomStatus(It.IsAny<object?>()), Times.Never);
        context.Verify(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReviewerNeverCallsTheBusinessApplyActivity()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<DateTime, CancellationToken>((_, token) => CancelableTimer(token));
        context.Setup(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Decision("finance", "Approve", 3));
        context.Setup(item => item.CallActivityAsync<string>(
                nameof(ApplyDecision), new ApplyDecisionRequest("sunny-soda:v3:approved"),
                It.IsAny<TaskOptions?>()))
            .ThrowsAsync(new InvalidOperationException("Apply failed"));

        ApprovalResult result = await new ApprovalStep().RunAsync(context.Object, Request);
        Assert.Equal(Approved, result);
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()), Times.Never);
    }

    [Theory]
    [InlineData("Approve", "Approved")]
    [InlineData("Reject", "Rejected")]
    [InlineData("Return", "Returned")]
    public async Task ChainChildUsesItsStageAndNeverAppliesBusinessDecision(string outcome, string state)
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        ApprovalStepContext step = new("chain-001", "client", "account", "workflow", "policy", 2,
            "Legal-review", 2, 3, "legal", "corr-001");
        context.Setup(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Decision("finance", outcome, 3, "Because"));

        ApprovalResult result = await new ApprovalStep().RunAsync(context.Object, Request with { Step = step });

        Assert.Equal(new ApprovalResult(state, 3, "finance", StartOffset, "Because"), result);
        context.Verify(item => item.SetCustomStatus(new ApprovalProgress("Waiting", "Legal-review", 2, 3)), Times.Once);
        context.Verify(item => item.SetCustomStatus(new ApprovalProgress(state, "Legal-review", 2, 3, 1)), Times.Once);
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()), Times.Never);
    }

    [Fact]
    public async Task ValidApprovalWaitsForTimerCancellationBeforeApplyingOrReturning()
    {
        Mock<ILogger> logger = new();
        Mock<TaskOrchestrationContext> context = CreateContext(logger.Object);
        TaskCompletionSource timer = new();
        CancellationToken timerToken = default;
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime, CancellationToken>((_, token) => timerToken = token)
            .Returns(timer.Task);
        context.Setup(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Decision("finance", "Approve", 3));

        Task<ApprovalResult> workflow = new ApprovalStep().RunAsync(context.Object, Request);

        Assert.True(timerToken.IsCancellationRequested);
        Assert.False(timer.Task.IsCompleted);
        Assert.False(workflow.IsCompleted);
        AssertWaitingWithoutApply(context);
        timer.SetCanceled(timerToken);
        Assert.Equal("Approved", (await workflow).Status);
        Assert.True(timer.Task.IsCanceled);
        VerifyLog(logger, LogLevel.Information, "approval timer canceled", Times.Once());
        context.Verify(item => item.CreateTimer(Start.AddMinutes(15), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TimerFaultDuringCancellationIsNotSwallowedAsSuccessfulApproval()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        TaskCompletionSource timer = new();
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(timer.Task);
        context.Setup(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Decision("finance", "Approve", 3));

        Task<ApprovalResult> workflow = new ApprovalStep().RunAsync(context.Object, Request);
        timer.SetException(new InvalidOperationException("Cancellation failed"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow);
        Assert.Equal("Cancellation failed", error.Message);
        AssertWaitingWithoutApply(context);
    }

    [Fact]
    public async Task DecisionWaitFailureStillCancelsAndDrainsTimerAndPropagates()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        Task timer = Task.CompletedTask;
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<DateTime, CancellationToken>((_, token) => timer = CancelableTimer(token));
        context.Setup(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("Event wait failed"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ApprovalStep().RunAsync(context.Object, Request));

        Assert.Equal("Event wait failed", error.Message);
        Assert.True(timer.IsCanceled);
        AssertWaitingWithoutApply(context);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("client")]
    [InlineData("account")]
    [InlineData("workflow")]
    [InlineData("policy")]
    [InlineData("version")]
    [InlineData("stage")]
    [InlineData("role")]
    [InlineData("number")]
    [InlineData("count")]
    [InlineData("correlation")]
    public async Task InvalidStepMetadataCannotBypassBusinessApplication(string invalid)
    {
        ApprovalStepContext step = new("chain-001", "client", null, "workflow", "policy", 1, "review", 1, 1, "finance", "corr-001");
        step = invalid switch
        {
            "parent" => step with { ParentInstanceId = new string('p', 81) },
            "client" => step with { ClientId = "" },
            "account" => step with { AccountId = "bad/account" },
            "workflow" => step with { WorkflowCode = "bad/code" },
            "policy" => step with { PolicyId = "" },
            "version" => step with { PolicyVersion = 0 },
            "stage" => step with { Stage = "" },
            "role" => step with { Role = "" },
            "number" => step with { StageNumber = 2 },
            "correlation" => step with { CorrelationId = "bad id" },
            _ => step with { StageCount = 11 }
        };
        Mock<TaskOrchestrationContext> context = CreateContext();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => new ApprovalStep().RunAsync(context.Object, Request with { Step = step }));

        context.Verify(item => item.CallActivityAsync<string>(
            It.IsAny<TaskName>(), It.IsAny<object?>(), It.IsAny<TaskOptions?>()), Times.Never);
        context.Verify(item => item.SetCustomStatus(It.IsAny<object?>()), Times.Never);
    }

    [Theory]
    [InlineData("Reject", null)]
    [InlineData("Reject", "")]
    [InlineData("Return", "   ")]
    [InlineData("Approve", "x")]
    public async Task RejectingOrReturningNeedsACommentOtherwiseTheDecisionIsRefused(string outcome, string? comment)
    {
        Mock<ILogger> logger = new();
        Mock<TaskOrchestrationContext> context = CreateContext(logger.Object);
        bool valid = outcome == "Approve";
        TaskCompletionSource<Decision> next = new();
        context.SetupSequence(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Decision("finance", outcome, 3, comment))
            .Returns(next.Task);

        Task<ApprovalResult> workflow = new ApprovalStep().RunAsync(context.Object, Request);

        Assert.Equal(valid, workflow.IsCompleted);
        if (valid)
        {
            Assert.Equal("Approved", (await workflow).Status);
            return;
        }

        VerifyLog(logger, LogLevel.Warning, "RefusedComment", Times.Once());
        next.SetResult(new Decision("finance", outcome, 3, "Here is why"));
        Assert.Equal(outcome == "Reject" ? "Rejected" : "Returned", (await workflow).Status);
    }

    [Fact]
    public async Task ACommentOverTheLimitIsRefused()
    {
        Mock<ILogger> logger = new();
        Mock<TaskOrchestrationContext> context = CreateContext(logger.Object);
        TaskCompletionSource<Decision> next = new();
        context.SetupSequence(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Decision("finance", "Approve", 3, new string('x', ApprovalRules.MaxCommentLength + 1)))
            .Returns(next.Task);

        Task<ApprovalResult> workflow = new ApprovalStep().RunAsync(context.Object, Request);

        Assert.False(workflow.IsCompleted);
        VerifyLog(logger, LogLevel.Warning, "RefusedComment", Times.Once());
        next.SetResult(new Decision("finance", "Approve", 3, "Fine"));
        Assert.Equal("Fine", (await workflow).Comment);
    }

    [Fact]
    public async Task AFinishedStepRecordsWhoWhatWhenAndWhyBeforeItReturns()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        List<string> order = [];
        context.Setup(item => item.CallActivityAsync<string>(
                nameof(RecordDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()))
            .Callback<TaskName, object?, TaskOptions?>((_, _, _) => order.Add("recorded"))
            .ReturnsAsync("recorded");
        context.Setup(item => item.SetCustomStatus(It.IsAny<object?>()))
            .Callback<object?>(status =>
            {
                if (status is ApprovalProgress { State: "Returned" }) order.Add("status");
            });
        context.Setup(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Decision("FINANCE", "Return", 3, "Add the budget"));

        ApprovalResult result = await new ApprovalStep().RunAsync(context.Object, Request);

        ApprovalResult expected = new("Returned", 3, "finance", StartOffset, "Add the budget");
        Assert.Equal(expected, result);
        Assert.Equal(["recorded", "status"], order);
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(RecordDecision), new RecordDecisionRequest(Request, expected), It.IsAny<TaskOptions?>()), Times.Once);
    }

    [Fact]
    public async Task ACancelEventEndsTheWaitWithoutADeciderAndStopsTheTimer()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        TaskCompletionSource<string> cancel = new();
        CancellationToken timerToken = default;
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime, CancellationToken>((_, token) => timerToken = token)
            .Returns<DateTime, CancellationToken>((_, token) => CancelableTimer(token));
        context.Setup(item => item.WaitForExternalEvent<string>(
                ApprovalNames.CancelEvent, It.IsAny<CancellationToken>())).Returns(cancel.Task);
        context.Setup(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, token) => PendingUntilCanceled<Decision>(token));

        Task<ApprovalResult> workflow = new ApprovalStep().RunAsync(context.Object, Request);
        Assert.False(workflow.IsCompleted);
        cancel.SetResult("Rejected");
        ApprovalResult result = await workflow;

        ApprovalResult expected = new("Canceled", 3, DecidedAt: StartOffset);
        Assert.Equal(expected, result);
        Assert.True(timerToken.IsCancellationRequested);
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(RecordDecision), new RecordDecisionRequest(Request, expected), It.IsAny<TaskOptions?>()), Times.Once);
        context.Verify(item => item.SetCustomStatus(new ApprovalProgress("Canceled", "finance", 1, 1, 1)), Times.Once);
    }

    [Fact]
    public async Task RecordingFailureFailsTheStepInsteadOfLosingTheRecord()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        context.Setup(item => item.CallActivityAsync<string>(
                nameof(RecordDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()))
            .ThrowsAsync(new InvalidOperationException("Record failed"));
        context.Setup(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Decision("finance", "Approve", 3));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ApprovalStep().RunAsync(context.Object, Request));

        context.Verify(item => item.SetCustomStatus(new ApprovalProgress("Approved", "finance", 1, 1, 1)), Times.Never);
    }

    [Fact]
    public async Task RecordDecisionLogsTheFullRecordIncludingTheComment()
    {
        Mock<ILogger<RecordDecision>> logger = new();
        Mock<TaskActivityContext> context = new();
        context.SetupGet(item => item.InstanceId).Returns("chain-001-s1-r1");
        ApprovalResult result = new("Rejected", 3, "finance", StartOffset, "Budget is wrong");

        string status = await new RecordDecision(logger.Object).RunAsync(
            context.Object, new RecordDecisionRequest(Request, result));

        Assert.Equal("Rejected", status);
        VerifyLog(logger, LogLevel.Information, "Decision recorded", Times.Once());
        VerifyLog(logger, LogLevel.Information, "Budget is wrong", Times.Once());
        VerifyLog(logger, LogLevel.Information, "corr-001", Times.Once());
    }

    [Fact]
    public async Task RecordDecisionRejectsAnUnknownStatus()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new RecordDecision(Mock.Of<ILogger<RecordDecision>>())
            .RunAsync(Mock.Of<TaskActivityContext>(),
                new RecordDecisionRequest(Request, new ApprovalResult("Maybe", 3))));
    }

    [Fact]
    public void ConverterReadsLowerCaseDecisionsButPreservesPascalCaseSerialization()
    {
        var converter = new JsonDataConverter(ApprovalJson.CreateSerializerOptions());
        Decision? decision = converter.Deserialize<Decision>(
            """{"approver":"finance","outcome":"Approve","version":3}""");

        Assert.Equal(new Decision("finance", "Approve", 3), decision);
        using JsonDocument payload = JsonDocument.Parse(converter.Serialize(decision)!);
        Assert.Equal(["Approver", "Outcome", "Version", "Comment"],
            payload.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Null(ApprovalJson.CreateSerializerOptions().PropertyNamingPolicy);
    }

    [Fact]
    public void RequestRoundTripsThroughTheSdkConverterAndValidates()
    {
        var converter = new JsonDataConverter(ApprovalJson.CreateSerializerOptions());
        ApprovalRequest? request = converter.Deserialize<ApprovalRequest>(converter.Serialize(Request));

        Assert.Equal(Request, request);
        request!.Validate();
    }

    [Fact]
    public void ConverterIncludesPublicFieldsAndReadsTheirNamesCaseInsensitively()
    {
        var converter = new JsonDataConverter(ApprovalJson.CreateSerializerOptions());

        Assert.Equal("""{"Receipt":"sunny-soda:v3:approved"}""",
            converter.Serialize(new FieldPayload { Receipt = "sunny-soda:v3:approved" }));
        Assert.Equal("sunny-soda:v3:approved",
            converter.Deserialize<FieldPayload>("""{"receipt":"sunny-soda:v3:approved"}""")!.Receipt);
        Assert.True(ApprovalJson.CreateSerializerOptions().IncludeFields);
    }

    [Fact]
    public async Task ApplyAttemptIsLoggedBeforeTheDelayCompletes()
    {
        Mock<ILogger<ApplyDecision>> logger = new();
        Mock<TaskActivityContext> context = new();
        context.SetupGet(item => item.InstanceId).Returns("sunny-soda-v3-001");

        Task<string> apply = new ApplyDecision(logger.Object).RunAsync(
            context.Object, new ApplyDecisionRequest("sunny-soda:v3:approved", 1));

        Assert.False(apply.IsCompleted);
        VerifyLog(logger, LogLevel.Information, "ApplyDecision attempt", Times.Once());
        Assert.Equal("sunny-soda:v3:approved", await apply);
    }

    [Fact]
    public async Task RepeatedApplyAttemptsLogTheSameReceiptWithoutClaimingDeduplication()
    {
        Mock<ILogger<ApplyDecision>> logger = new();
        Mock<TaskActivityContext> context = new();
        ApplyDecision activity = new(logger.Object);
        ApplyDecisionRequest input = new("sunny-soda:v3:approved");

        Assert.Equal(input.Receipt, await activity.RunAsync(context.Object, input));
        Assert.Equal(input.Receipt, await activity.RunAsync(context.Object, input));

        VerifyLog(logger, LogLevel.Information, input.Receipt, Times.Exactly(2));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(61)]
    public async Task ActivityAlsoRejectsUnboundedDelays(int delay)
    {
        Mock<ILogger<ApplyDecision>> logger = new();

        await Assert.ThrowsAsync<ArgumentException>(() => new ApplyDecision(logger.Object).RunAsync(
            Mock.Of<TaskActivityContext>(), new ApplyDecisionRequest("sunny-soda:v3:approved", delay)));

        VerifyLog(logger, LogLevel.Information, "ApplyDecision attempt", Times.Never());
    }

    private static void AssertWaitingWithoutApply(Mock<TaskOrchestrationContext> context)
    {
        context.Verify(item => item.SetCustomStatus(Waiting), Times.Once);
        context.Verify(item => item.SetCustomStatus(
            It.Is<object?>(status => !Equals(status, Waiting))), Times.Never);
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()), Times.Never);
    }

    private static void VerifyLog<TLogger>(
        Mock<TLogger> logger, LogLevel level, string message, Times times) where TLogger : class, ILogger
    {
        logger.Verify(item => item.Log(
            level, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(message, StringComparison.Ordinal)),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);
    }

    private static Mock<TaskOrchestrationContext> CreateContext(ILogger? logger = null)
    {
        Mock<TaskOrchestrationContext> context = new();
        context.SetupGet(item => item.InstanceId).Returns("sunny-soda-v3-001");
        context.SetupGet(item => item.CurrentUtcDateTime).Returns(Start);
        context.Setup(item => item.CallActivityAsync<string>(
            nameof(RecordSubmission), It.IsAny<object?>(), It.IsAny<TaskOptions?>())).ReturnsAsync("sunny-soda");
        context.Setup(item => item.CallActivityAsync<string>(
            nameof(RecordDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>())).ReturnsAsync("recorded");
        context.Setup(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()))
            .ReturnsAsync("sunny-soda:v3:approved");
        context.Setup(item => item.WaitForExternalEvent<string>(
                ApprovalNames.CancelEvent, It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, token) => PendingUntilCanceled<string>(token));
        context.Setup(item => item.CreateReplaySafeLogger<ApprovalStep>()).Returns(logger ?? NullLogger.Instance);
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<DateTime, CancellationToken>((_, token) => CancelableTimer(token));
        return context;
    }

    private static Task<T> PendingUntilCanceled<T>(CancellationToken token)
    {
        TaskCompletionSource<T> pending = new();
        token.Register(() => pending.TrySetCanceled(token));
        return pending.Task;
    }

    private static Task CancelableTimer(CancellationToken token)
    {
        TaskCompletionSource timer = new();
        token.Register(() => timer.TrySetCanceled(token));
        return timer.Task;
    }

    private sealed class FieldPayload
    {
        public string Receipt = string.Empty;
    }
}
