using Approval.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Approval.Console;

public static class PolicyEndpoints
{
    public static void MapPolicies(this WebApplication app)
    {
        app.MapGet("/api/demo-identities", async Task<Ok<DemoIdentityRegistry>> (
            IDemoIdentityStore store, CancellationToken cancellation) =>
                TypedResults.Ok(await store.ListAsync(cancellation)))
            .WithSummary("List simulator role assignments. These are not approval-policy rules or real authorization.");
        app.MapPost("/api/demo-identities", async Task<Ok<DemoIdentityRegistry>> (
            SaveDemoIdentitiesRequest request, IDemoIdentityStore store, CancellationToken cancellation) =>
                TypedResults.Ok(await store.SaveAsync(request, cancellation)))
            .WithSummary("Replace demo actors for a client/account without changing policy versions.");
        app.MapGet("/api/policies", async Task<Ok<PolicyRegistry>> (
            IPolicyStore store, CancellationToken cancellation) =>
                TypedResults.Ok(await store.ListAsync(cancellation)))
            .WithSummary("List immutable saved versions and active selections.");
        app.MapPost("/api/policies", async Task<Results<Created<ApprovalPolicy>, Ok<ApprovalPolicy>>> (
            SavePolicyRequest request, IPolicyStore store, CancellationToken cancellation) =>
        {
            ApprovalPolicy policy = await store.SaveAsync(request, cancellation);
            return policy.Version == request.BaseVersion
                ? TypedResults.Ok(policy)
                : TypedResults.Created("/api/policies", policy);
        }).WithSummary("Save changed content as a new active version. Unchanged content returns the existing version.");
        app.MapPost("/api/policies/{id}/activate", async Task<Ok<ApprovalPolicy>> (
            string id, ActivatePolicyRequest request, IPolicyStore store, CancellationToken cancellation) =>
                TypedResults.Ok(await store.ActivateAsync(id, request.Version, cancellation)))
            .WithSummary("Activate a saved version for future workflow starts.");
        app.MapPost("/api/workflows", async Task<Created<object>> (
            StartChainRequest request, IApprovalChainService service, CancellationToken cancellation) =>
                TypedResults.Created($"/api/workflows/{request.InstanceId}", await service.StartAsync(request, cancellation)))
            .WithSummary("Start a chain using a resolved, pinned policy snapshot.");
        app.MapGet("/api/workflows", async Task<Ok<IReadOnlyList<WorkflowSummaryResponse>>> (
            IApprovalChainService service, CancellationToken cancellation) =>
                TypedResults.Ok(await service.ListAsync(cancellation)))
            .WithSummary("List workflow instances for the approval overview.");
        app.MapGet("/api/workflows/{id}/approvals", async Task<Ok<IReadOnlyList<WorkflowApprovalResponse>>> (
            string id, IApprovalChainService service, CancellationToken cancellation) =>
                TypedResults.Ok(await service.StepsAsync(id, cancellation)))
            .WithSummary("Show every pinned reviewer step, including blocked future stages and completed steps.");
        app.MapGet("/api/workflows/{id}", async Task<Ok<object>> (
            string id, IApprovalChainService service, CancellationToken cancellation) =>
                TypedResults.Ok(await service.StatusAsync(id, cancellation)))
            .WithSummary("Show the pinned policy, stage progress, and aggregate business result.");
    }
}
