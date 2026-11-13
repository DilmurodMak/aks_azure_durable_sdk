using Microsoft.AspNetCore.Http.HttpResults;

namespace Approval.Console;

public static class ApprovalEndpoints
{
    public static void MapApprovals(this WebApplication app, ConsoleEnvironmentResponse environment)
    {
        app.MapGet("/api/environment", () => TypedResults.Ok(environment))
            .WithSummary("Show the selected backend and spike-only identity warning.");
        app.MapGet("/api/approvals", async Task<Ok<IReadOnlyList<WaitingApprovalResponse>>> (
            IApprovalConsoleService service, CancellationToken cancellation) =>
                TypedResults.Ok(await service.ListAsync(cancellation)))
            .WithSummary("List waiting approvals.");
        app.MapPost("/api/approvals", async Task<Created<object>> (
            CreateApprovalRequest request, IApprovalConsoleService service, CancellationToken cancellation) =>
                TypedResults.Created($"/api/approvals/{request.InstanceId}", await service.StartAsync(request, cancellation)))
            .WithSummary("Start a fresh approval with explicit demo participants.")
            .ProducesProblem(400).ProducesProblem(409);
        app.MapPost("/api/approvals/{id}/decision", async Task<Accepted<object>> (
            string id, DecisionForm decision, IApprovalConsoleService service, CancellationToken cancellation) =>
                TypedResults.Accepted($"/api/approvals/{id}", await service.DecideAsync(id, decision, cancellation)))
            .WithSummary("Send a demo decision to a waiting instance; accepted does not mean approved.")
            .ProducesProblem(400).ProducesProblem(409);
        app.MapGet("/api/approvals/{id}", async Task<Ok<object>> (
            string id, bool? history, IApprovalConsoleService service, CancellationToken cancellation) =>
                TypedResults.Ok(await service.StatusAsync(id, history ?? false, cancellation)))
            .WithSummary("Get runtime/business status and optionally durable history.")
            .ProducesProblem(404);
    }
}
