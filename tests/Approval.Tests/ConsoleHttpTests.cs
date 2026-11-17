using System.Net;
using System.Net.Http.Json;
using Approval.Console;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Approval.Tests;

public sealed class ConsoleHttpTests
{
    [Fact]
    public async Task HttpContractAndBrowserGuardsAreEnforced()
    {
        Mock<IApprovalConsoleService> service = new();
        Mock<IApprovalChainService> chain = new();
        chain.Setup(item => item.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowSummaryResponse>());
        chain.Setup(item => item.StepsAsync("console-test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowApprovalResponse>());
        chain.Setup(item => item.StartAsync(It.IsAny<StartChainRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new { instanceId = "console-test" });
        service.Setup(item => item.DecideAsync("console-test", It.IsAny<DecisionForm>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new { status = "DecisionSentNotYetApplied" });
        service.Setup(item => item.StatusAsync("missing", false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());
        await using ConsoleFactory factory = new(service.Object, chain.Object);
        using HttpClient client = factory.CreateClient(new() { BaseAddress = new Uri("http://127.0.0.1:5080") });
        client.DefaultRequestHeaders.Add("X-Approval-Console", "spike");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/environment")).StatusCode);
        HttpResponseMessage script = await client.GetAsync("/console.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.True(script.Headers.CacheControl?.NoCache);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/workflows")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/workflows/console-test/approvals")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        var start = new { instanceId = "console-test", planId = "sunny-soda", version = 3, requester = "requester", clientId = "client", workflowCode = "B07", budgetAmount = 7500 };
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/workflows", start);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/workflows/console-test", created.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/workflows",
            new { instanceId = "missing-budget", planId = "plan", version = 1, requester = "requester", clientId = "client", workflowCode = "B07" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/approvals/console-test/decision",
            new { actingAs = "finance", outcome = "Approve", version = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/approvals/console-test/decision",
            new { actingAs = "", outcome = "Approve", version = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/approvals/console-test/decision",
            new { actingAs = "finance", outcome = "Approve", version = 3, approver = "spoof" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/approvals/missing")).StatusCode);

        client.DefaultRequestHeaders.Remove("X-Approval-Console");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/workflows", start)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Approval-Console", "spike");
        client.DefaultRequestHeaders.Add("Origin", "https://unrelated.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/workflows", start)).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Host = "unrelated.invalid";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/workflows")).StatusCode);
    }

    private sealed class ConsoleFactory(IApprovalConsoleService service, IApprovalChainService chain) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("ENDPOINT", "http://localhost:8080");
            Environment.SetEnvironmentVariable("TASKHUB", "approval-local");
            Environment.SetEnvironmentVariable("SCHEDULER_AUTH", "None");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IApprovalConsoleService>();
                services.AddSingleton(service);
                services.RemoveAll<IApprovalChainService>();
                services.AddSingleton(chain);
                services.AddTransient<IStartupFilter, LoopbackTestFilter>();
            });
        }
    }

    private sealed class LoopbackTestFilter : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
            Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
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
