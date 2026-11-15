using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Approval.Console;
using Approval.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Approval.Tests;

/// <summary>What the console refuses or records when a plan is submitted for approval.</summary>
public sealed class StartRulesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"start-rules-test-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("assigned-finance", "REQUESTER", "assigned-owner")]
    [InlineData("requester", "assigned-legal", "assigned-owner")]
    [InlineData("assigned-finance", "assigned-legal", "Assigned-Finance")]
    public async Task ARequesterInTheChainOrAPersonInTwoStagesIsRefusedBeforeScheduling(
        string finance, string legal, string owner)
    {
        (ApprovalChainService service, Mock<DurableTaskClient> client) = await Setup(finance, legal, owner);

        SeparationOfDutiesException conflict = await Assert.ThrowsAsync<SeparationOfDutiesException>(
            () => service.StartAsync(Request(), default));

        Assert.NotEmpty(conflict.Message);
        client.Verify(item => item.ScheduleNewOrchestrationInstanceAsync(
            It.IsAny<TaskName>(), It.IsAny<object?>(), It.IsAny<StartOrchestrationOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ACorrelationIdIsGeneratedWhenOmittedAndCarriedIntoTheInputAndTags()
    {
        (ApprovalChainService service, Mock<DurableTaskClient> client) = await Setup();
        List<(ApprovalChainRequest Input, IReadOnlyDictionary<string, string> Tags)> starts = Capture(client);

        string first = Correlation(await service.StartAsync(Request(), default));
        string second = Correlation(await service.StartAsync(Request() with { InstanceId = "run-two" }, default));

        Assert.NotEqual(first, second);
        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.Equal(first, starts[0].Input.CorrelationId);
        Assert.Equal(first, starts[0].Tags["correlationId"]);
        Assert.Equal("requester", starts[0].Tags["startedBy"]);
        Assert.Equal("account", starts[0].Tags["account"]);
        Assert.Equal("client", starts[0].Tags["client"]);
        Assert.Equal("B07", starts[0].Tags["workflow"]);
    }

    [Fact]
    public async Task ACallerSuppliedCorrelationIdIsKept()
    {
        (ApprovalChainService service, Mock<DurableTaskClient> client) = await Setup();
        List<(ApprovalChainRequest Input, IReadOnlyDictionary<string, string> Tags)> starts = Capture(client);

        string id = Correlation(await service.StartAsync(Request() with { CorrelationId = "crm-ticket-4711" }, default));

        Assert.Equal("crm-ticket-4711", id);
        Assert.Equal("crm-ticket-4711", starts[0].Input.CorrelationId);
    }

    [Theory]
    [InlineData("bad id")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task AnInvalidCorrelationIdIsRefusedBeforeScheduling(string correlationId)
    {
        (ApprovalChainService service, Mock<DurableTaskClient> client) = await Setup();

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.StartAsync(Request() with { CorrelationId = correlationId }, default));

        client.Verify(item => item.ScheduleNewOrchestrationInstanceAsync(
            It.IsAny<TaskName>(), It.IsAny<object?>(), It.IsAny<StartOrchestrationOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheStartAndDecisionSpansCarryTheBusinessTags()
    {
        List<System.Diagnostics.Activity> spans = [];
        using System.Diagnostics.ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == ApprovalTelemetry.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (spans) spans.Add(activity); }
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        (ApprovalChainService service, Mock<DurableTaskClient> client) = await Setup();
        Capture(client);

        await service.StartAsync(Request() with { CorrelationId = "corr-span" }, default);

        System.Diagnostics.Activity span = Assert.Single(spans.ToArray(), item =>
            item.OperationName == "approval.start" && Equals(item.GetTagItem("approval.correlationId"), "corr-span"));
        Assert.Equal("corr-span", span.GetTagItem("approval.correlationId"));
        Assert.Equal("requester", span.GetTagItem("approval.startedBy"));
        Assert.Equal("account", span.GetTagItem("approval.account"));
        Assert.Equal("client", span.GetTagItem("approval.client"));
    }

    [Fact]
    public void ASpanStartedUnderAnUnrecordedHttpParentBecomesARecordedRoot()
    {
        // Mimics a parent-based sampler: children of an unrecorded parent are dropped, roots are kept.
        using System.Diagnostics.ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == ApprovalTelemetry.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> options) =>
                options.Parent.TraceId != default && !options.Parent.TraceFlags.HasFlag(System.Diagnostics.ActivityTraceFlags.Recorded)
                    ? System.Diagnostics.ActivitySamplingResult.None
                    : System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        using System.Diagnostics.Activity httpRequest = new("propagation-only-http-request");
        httpRequest.Start();
        Assert.False(httpRequest.Recorded);

        using System.Diagnostics.Activity? span = ApprovalTelemetry.StartSpan("approval.start",
            new Dictionary<string, string> { ["correlationId"] = "corr-root" });

        Assert.NotNull(span);
        Assert.True(span.Recorded);
        Assert.Null(span.ParentId);
        Assert.NotEqual(httpRequest.TraceId, span.TraceId);
    }

    [Fact]
    public async Task TheHttpApiMapsASeparationConflictTo409AndAMissingCommentTo400()
    {
        Mock<IApprovalConsoleService> console = new();
        Mock<IApprovalChainService> chain = new();
        chain.Setup(item => item.StartAsync(It.IsAny<StartChainRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SeparationOfDutiesException("The requester holds a required role."));
        console.Setup(item => item.DecideAsync(It.IsAny<string>(), It.IsAny<DecisionForm>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("A comment is required to reject or return a plan."));
        await using StartRulesFactory factory = new(console.Object, chain.Object);
        using HttpClient http = factory.CreateClient(new() { BaseAddress = new Uri("http://127.0.0.1:5080") });
        http.DefaultRequestHeaders.Add("X-Approval-Console", "spike");

        HttpResponseMessage start = await http.PostAsJsonAsync("/api/workflows", new
        {
            instanceId = "run",
            planId = "plan",
            version = 1,
            requester = "requester",
            clientId = "client",
            workflowCode = "B07"
        });
        HttpResponseMessage decision = await http.PostAsJsonAsync("/api/approvals/run-s1-r1/decision",
            new { actingAs = "finance", outcome = "Reject", version = 1 });

        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        Assert.Equal("The requester holds a required role.", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, decision.StatusCode);
    }

    [Fact]
    public async Task ACommentIsAcceptedOnTheDecisionForm()
    {
        Mock<IApprovalConsoleService> console = new();
        Mock<IApprovalChainService> chain = new();
        console.Setup(item => item.DecideAsync("run-s1-r1",
                It.Is<DecisionForm>(form => form.Comment == "Budget is wrong"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new { status = "DecisionSentNotYetApplied" });
        await using StartRulesFactory factory = new(console.Object, chain.Object);
        using HttpClient http = factory.CreateClient(new() { BaseAddress = new Uri("http://127.0.0.1:5080") });
        http.DefaultRequestHeaders.Add("X-Approval-Console", "spike");

        HttpResponseMessage decision = await http.PostAsJsonAsync("/api/approvals/run-s1-r1/decision",
            new { actingAs = "finance", outcome = "Reject", version = 1, comment = "Budget is wrong" });

        Assert.Equal(HttpStatusCode.Accepted, decision.StatusCode);
    }

    private async Task<(ApprovalChainService, Mock<DurableTaskClient>)> Setup(
        string finance = "assigned-finance", string legal = "assigned-legal", string owner = "assigned-owner")
    {
        PolicyStore policies = new(Path.Combine(directory, "policies.json"));
        DemoIdentityStore identities = new(Path.Combine(directory, "identities.json"));
        await policies.SaveAsync(new()
        {
            Id = "policy",
            ClientId = "client",
            AccountId = "account",
            DefaultStages = [new("review", ["Finance", "Legal"]), new("release", ["Owner"])]
        }, default);
        await identities.SaveAsync(new()
        {
            ClientId = "client",
            AccountId = "account",
            Assignments = new() { ["Finance"] = finance, ["Legal"] = legal, ["Owner"] = owner }
        }, default);
        Mock<DurableTaskClient> client = new("test");
        client.Setup(item => item.GetInstanceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrchestrationMetadata?)null);
        return (new ApprovalChainService(client.Object, policies, identities), client);
    }

    private static StartChainRequest Request() => new()
    {
        InstanceId = "run-one",
        ClientId = "client",
        AccountId = "account",
        WorkflowCode = "B07",
        PlanId = "sunny-soda",
        Version = 3,
        Requester = "requester"
    };

    private static List<(ApprovalChainRequest, IReadOnlyDictionary<string, string>)> Capture(Mock<DurableTaskClient> client)
    {
        List<(ApprovalChainRequest, IReadOnlyDictionary<string, string>)> starts = [];
        client.Setup(item => item.ScheduleNewOrchestrationInstanceAsync(
                It.IsAny<TaskName>(), It.IsAny<object?>(), It.IsAny<StartOrchestrationOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((TaskName _, object? input, StartOrchestrationOptions? options, CancellationToken _) =>
            {
                starts.Add((Assert.IsType<ApprovalChainRequest>(input), options!.Tags));
                return Task.FromResult(options.InstanceId!);
            });
        return starts;
    }

    private static string Correlation(object started) =>
        (string)started.GetType().GetProperty("correlationId")!.GetValue(started)!;

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class StartRulesFactory(IApprovalConsoleService console, IApprovalChainService chain)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("ENDPOINT", "http://localhost:8080");
            Environment.SetEnvironmentVariable("TASKHUB", "approval-local");
            Environment.SetEnvironmentVariable("SCHEDULER_AUTH", "None");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IApprovalConsoleService>();
                services.AddSingleton(console);
                services.RemoveAll<IApprovalChainService>();
                services.AddSingleton(chain);
                services.AddTransient<IStartupFilter, LoopbackFilter>();
            });
        }
    }

    private sealed class LoopbackFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, run) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                await run(context);
            });
            next(app);
        };
    }
}
