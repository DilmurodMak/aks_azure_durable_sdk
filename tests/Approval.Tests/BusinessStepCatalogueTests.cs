using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Approval.Console;
using Approval.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Approval.Tests;

public sealed class BusinessStepCatalogueTests
{
    [Fact]
    public void CatalogueHasUniqueValidCodesAndTheGate2Step()
    {
        Assert.NotEmpty(BusinessStepCatalogue.All);
        Assert.Equal(BusinessStepCatalogue.All.Count, BusinessStepCatalogue.All.Select(step => step.Code).Distinct().Count());
        foreach (BusinessStep step in BusinessStepCatalogue.All)
        {
            ApprovalPolicy.ValidateKey(step.Code, "Code");
            Assert.False(string.IsNullOrWhiteSpace(step.Name));
            if (step.PhaseCode != "X") Assert.StartsWith(step.PhaseCode, step.PhaseName);
        }

        BusinessStep gate2 = Assert.Single(BusinessStepCatalogue.All, step => step.Code == "B07");
        Assert.Contains("Gate 2", gate2.Name);
        Assert.Equal("P3", gate2.PhaseCode);
    }

    [Theory]
    [InlineData("B07", true)]
    [InlineData("Gate2", false)]
    [InlineData("b07", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyCatalogueCodesAreKnown(string? code, bool known) =>
        Assert.Equal(known, BusinessStepCatalogue.Contains(code));

    [Fact]
    public async Task ApiListsStepsRejectsUnknownOverrideStepsAndExplainsWhichChainApplies()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"approval-steps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            await using IsolatedFactory factory = new(folder);
            using HttpClient client = factory.CreateClient(new() { BaseAddress = new Uri("http://127.0.0.1:5080") });
            client.DefaultRequestHeaders.Add("X-Approval-Console", "spike");
            JsonSerializerOptions web = new(JsonSerializerDefaults.Web);
            var finance = new[] { new { name = "review", roles = new[] { "Finance" }, timeoutMinutes = 15 } };
            var client2 = new[] { new { name = "sign-off", roles = new[] { "Client" }, timeoutMinutes = 15 } };

            BusinessStep[] steps = (await client.GetFromJsonAsync<BusinessStep[]>("/api/business-steps", web))!;
            Assert.Equal(BusinessStepCatalogue.All.Count, steps.Length);
            Assert.Contains(steps, step => step.Code == "B07");

            HttpResponseMessage rejected = await client.PostAsJsonAsync("/api/policies", new
            {
                id = "typo-policy",
                clientId = "client",
                baseVersion = 0,
                defaultStages = finance,
                overrides = new[] { new { workflowCode = "Gate2", stages = client2 } }
            });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.False(File.Exists(Path.Combine(folder, "policies.json")));
            HttpResponseMessage unknownMember = await client.PostAsJsonAsync("/api/policies", new
            {
                id = "unknown-member",
                clientId = "client",
                workflowCode = "B07",
                baseVersion = 0,
                stages = finance
            });
            Assert.Equal(HttpStatusCode.BadRequest, unknownMember.StatusCode);

            HttpResponseMessage saved = await client.PostAsJsonAsync("/api/policies", new
            {
                id = "good-policy",
                clientId = "client",
                baseVersion = 0,
                defaultStages = finance,
                overrides = new[] { new { workflowCode = "B07", stages = client2 } }
            });
            Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

            PolicyMatchResponse gate2 = (await client.GetFromJsonAsync<PolicyMatchResponse>(
                "/api/policy-match?clientId=client&workflowCode=B07", web))!;
            PolicyMatchResponse other = (await client.GetFromJsonAsync<PolicyMatchResponse>(
                "/api/policy-match?clientId=client&workflowCode=B08", web))!;
            Assert.Equal(("good-policy", 1, "client override", "sign-off"),
                (gate2.PolicyId, gate2.PolicyVersion, gate2.Source, gate2.Stages[0].Name));
            Assert.Equal(("client default", "review"), (other.Source, other.Stages[0].Name));

            HttpResponseMessage none = await client.GetAsync("/api/policy-match?clientId=unknown&workflowCode=B07");
            Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
            Assert.Contains("No active approval policy", await none.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("POLICY_STORE_PATH", null);
            Environment.SetEnvironmentVariable("DEMO_IDENTITIES_PATH", null);
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class IsolatedFactory(string folder) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("ENDPOINT", "http://localhost:8080");
            Environment.SetEnvironmentVariable("TASKHUB", "approval-local");
            Environment.SetEnvironmentVariable("SCHEDULER_AUTH", "None");
            Environment.SetEnvironmentVariable("POLICY_STORE_PATH", Path.Combine(folder, "policies.json"));
            Environment.SetEnvironmentVariable("DEMO_IDENTITIES_PATH", Path.Combine(folder, "identities.json"));
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, LoopbackFilter>());
        }
    }

    private sealed class LoopbackFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, run) =>
            {
                context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
                await run(context);
            });
            next(app);
        };
    }
}
