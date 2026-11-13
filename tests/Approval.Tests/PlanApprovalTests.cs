using System.Text.Json;
using Approval.Contracts;
using Approval.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Converters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Approval.Tests;

public sealed class PlanApprovalTests
{
    private static readonly ApprovalRequest Request = new("sunny-soda", 3, "requester", "finance", 15);
    private static readonly DateTime Start = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

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
            .Returns(new TaskCompletionSource().Task);
        context.SetupSequence(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Decision(approver!, outcome!, version))
            .Returns(validDecision.Task);

        Task<ApprovalResult> workflow = new PlanApproval().RunAsync(context.Object, Request);

        Assert.False(workflow.IsCompleted);
        Assert.False(timerToken.IsCancellationRequested);
        AssertWaitingWithoutApply(context);
        context.Verify(item => item.CreateReplaySafeLogger<PlanApproval>(), Times.Once);
        VerifyLog(logger, LogLevel.Warning, "refused decision", Times.Once());
        VerifyLog(logger, LogLevel.Warning, ApprovalNames.WaitingStatus, Times.Once());

        validDecision.SetResult(new Decision("FINANCE", "Approve", 3));
        ApprovalResult result = await workflow;

        Assert.Equal(new ApprovalResult("Approved", 3, "sunny-soda:v3:approved"), result);
        Assert.True(timerToken.IsCancellationRequested);
        context.Verify(item => item.CreateTimer(Start.AddMinutes(15), It.IsAny<CancellationToken>()), Times.Once);
        context.Verify(item => item.SetCustomStatus(ApprovalNames.WaitingStatus), Times.Once);
        context.Verify(item => item.SetCustomStatus("Approved"), Times.Once);
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), new ApplyDecisionRequest("sunny-soda:v3:approved", 0),
            It.IsAny<TaskOptions?>()), Times.Once);
    }

    [Fact]
    public async Task NullDecisionIsRefusedUntilAnExactRejection()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        CancellationToken timerToken = default;
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime, CancellationToken>((_, token) => timerToken = token)
            .Returns(new TaskCompletionSource().Task);
        context.SetupSequence(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Decision)null!)
            .ReturnsAsync(new Decision("finance", "Reject", 3));

        ApprovalResult result = await new PlanApproval().RunAsync(context.Object, Request);

        Assert.Equal(new ApprovalResult("Rejected", 3, null), result);
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
        List<string?> statuses = [];
        context.Setup(item => item.SetCustomStatus(It.IsAny<object?>()))
            .Callback<object?>(status => statuses.Add(status as string));
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

        Task<ApprovalResult> workflow = new PlanApproval().RunAsync(context.Object, Request);

        Assert.False(workflow.IsCompleted);
        Assert.Equal([ApprovalNames.WaitingStatus], statuses);
        Assert.Equal([Start.AddMinutes(15)], deadlines);
        Assert.Equal(5, waits);
        Assert.False(eventToken.IsCancellationRequested);
        AssertWaitingWithoutApply(context);
        VerifyLog(logger, LogLevel.Warning, "refused decision", Times.Exactly(4));

        timer.SetResult();
        ApprovalResult result = await workflow;

        Assert.Equal(new ApprovalResult("Timeout", 3, null), result);
        Assert.True(eventToken.IsCancellationRequested);
        Assert.Equal([ApprovalNames.WaitingStatus, "Timeout"], statuses);
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

        ApprovalResult result = await new PlanApproval().RunAsync(context.Object, Request);

        Assert.Equal(new ApprovalResult("Timeout", 3, null), result);
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

        ApprovalResult result = await new PlanApproval().RunAsync(context.Object, Request);

        Assert.Equal(new ApprovalResult("Timeout", 3, null), result);
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
            () => new PlanApproval().RunAsync(context.Object, Request));

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

        Task<ApprovalResult> workflow = new PlanApproval().RunAsync(context.Object, Request);
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
            () => new PlanApproval().RunAsync(context.Object, Request));

        context.Verify(item => item.SetCustomStatus(It.IsAny<object?>()), Times.Never);
        context.Verify(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyFailureIsNotReportedAsApprovalAndReceivesTheRequestedDelay()
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        context.Setup(item => item.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource().Task);
        context.Setup(item => item.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Decision("finance", "Approve", 3));
        context.Setup(item => item.CallActivityAsync<string>(
                nameof(ApplyDecision), new ApplyDecisionRequest("sunny-soda:v3:approved", 60),
                It.IsAny<TaskOptions?>()))
            .ThrowsAsync(new InvalidOperationException("Apply failed"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PlanApproval().RunAsync(context.Object, Request with { ApplyDecisionDelaySeconds = 60 }));

        Assert.Equal("Apply failed", error.Message);
        context.Verify(item => item.SetCustomStatus("Approved"), Times.Never);
        context.Verify(item => item.CallActivityAsync<string>(
            nameof(ApplyDecision), new ApplyDecisionRequest("sunny-soda:v3:approved", 60),
            It.IsAny<TaskOptions?>()), Times.Once);
    }

    [Fact]
    public void ConverterReadsLowerCaseDecisionsButPreservesPascalCaseSerialization()
    {
        var converter = new JsonDataConverter(ApprovalJson.CreateSerializerOptions());
        Decision? decision = converter.Deserialize<Decision>(
            """{"approver":"finance","outcome":"Approve","version":3}""");

        Assert.Equal(new Decision("finance", "Approve", 3), decision);
        using JsonDocument payload = JsonDocument.Parse(converter.Serialize(decision)!);
        Assert.Equal(["Approver", "Outcome", "Version"],
            payload.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Null(ApprovalJson.CreateSerializerOptions().PropertyNamingPolicy);
    }

    [Fact]
    public void ActivityDelayDefaultsToZero()
    {
        ApprovalRequest? request = new JsonDataConverter(ApprovalJson.CreateSerializerOptions()).Deserialize<ApprovalRequest>(
            """{"PlanId":"sunny-soda","Version":3,"Requester":"requester","Approver":"finance","TimeoutMinutes":15}""");

        Assert.Equal(Request, request);
        Assert.Equal(0, request!.ApplyDecisionDelaySeconds);
        request.Validate();
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

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void DelayValidationAcceptsBothBounds(int delay)
    {
        (Request with { ApplyDecisionDelaySeconds = delay }).Validate();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(61)]
    [InlineData(int.MaxValue)]
    public async Task InvalidDelayIsRejectedBeforeAnyWorkflowActivity(int delay)
    {
        Mock<TaskOrchestrationContext> context = CreateContext();
        ApprovalRequest request = Request with { ApplyDecisionDelaySeconds = delay };

        Assert.Throws<ArgumentException>(request.Validate);
        await Assert.ThrowsAsync<ArgumentException>(
            () => new PlanApproval().RunAsync(context.Object, request));
        context.Verify(item => item.CallActivityAsync<string>(
            It.IsAny<TaskName>(), It.IsAny<object?>(), It.IsAny<TaskOptions?>()), Times.Never);
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
        context.Verify(item => item.SetCustomStatus(ApprovalNames.WaitingStatus), Times.Once);
        context.Verify(item => item.SetCustomStatus(
            It.Is<object?>(status => !Equals(status, ApprovalNames.WaitingStatus))), Times.Never);
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
            nameof(ApplyDecision), It.IsAny<object?>(), It.IsAny<TaskOptions?>()))
            .ReturnsAsync("sunny-soda:v3:approved");
        context.Setup(item => item.CreateReplaySafeLogger<PlanApproval>()).Returns(logger ?? NullLogger.Instance);
        return context;
    }

    private sealed class FieldPayload
    {
        public string Receipt = string.Empty;
    }
}
