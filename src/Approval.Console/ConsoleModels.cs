using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Approval.Contracts;

namespace Approval.Console;

/// <summary>Demonstrates a decision from a user-selected actor; this is not production authorization.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DecisionForm
{
    [Required] public required string ActingAs { get; init; }
    [Required] public required string Outcome { get; init; }
    [Range(1, int.MaxValue)] public int Version { get; init; }

    /// <summary>Why: required to reject or return, recommended otherwise. At most 1000 characters.</summary>
    public string? Comment { get; init; }
}

/// <summary>A workflow available for viewing its complete pinned approval plan.</summary>
public sealed record WorkflowSummaryResponse(
    string InstanceId, string PlanId, int Version, string RuntimeStatus, int PolicyVersion, DateTimeOffset CreatedAt,
    string WorkflowCode, string ChainSource, string Requester, string CorrelationId);

/// <summary>Which chain would apply if a workflow started now for the client, account, and business step.</summary>
public sealed record PolicyMatchResponse(
    string PolicyId, int PolicyVersion, string WorkflowCode, string Source, PolicyStage[] Stages);

/// <summary>A planned reviewer step, including steps that have not yet been started by the worker.</summary>
public sealed record WorkflowApprovalResponse(
    string InstanceId, string ParentInstanceId, string PlanId, int Version, string Requester, string Approver,
    string Role, int PolicyVersion, string Rank, string State, bool CanDecide, bool CanView,
    string? BlockedReason, ApprovalProgress Progress, string? DecidedBy, DateTimeOffset? DecidedAt, string? Comment);

/// <summary>Identifies the actual backend selected when this process started.</summary>
public sealed record ConsoleEnvironmentResponse(
    string Environment, string Endpoint, string TaskHub, string DashboardUrl, string Warning);

/// <summary>
/// Save a new immutable policy set version for a client or account: an optional default chain for every
/// business step plus per-step overrides. Stages run in order; roles in one stage run in parallel.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SavePolicyRequest
{
    [Required] public required string Id { get; init; }
    [Required] public required string ClientId { get; init; }
    public string? AccountId { get; init; }
    [Range(0, int.MaxValue - 1)] public int BaseVersion { get; init; }

    /// <summary>The chain for every business step without an override. Null means no default.</summary>
    public PolicyStage[]? DefaultStages { get; init; }

    public StepOverride[] Overrides { get; init; } = [];

    /// <summary>Overrides must target business steps from the catalogue.</summary>
    public void ValidateSteps()
    {
        foreach (StepOverride item in Overrides ?? [])
        {
            if (!BusinessStepCatalogue.Contains(item?.WorkflowCode))
                throw new ArgumentException(
                    "Each override must target a business step from the platform analysis, for example B07. Use GET /api/business-steps.");
        }
    }
}

/// <summary>Activate an existing saved policy version for future workflow starts only.</summary>
public sealed record ActivatePolicyRequest([property: Range(1, int.MaxValue)] int Version);

/// <summary>Start a workflow using the policy selected for its client, account, and business step.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StartChainRequest
{
    [Required] public required string InstanceId { get; init; }
    [Required] public required string ClientId { get; init; }
    public string? AccountId { get; init; }
    [Required] public required string WorkflowCode { get; init; }
    [Required] public required string PlanId { get; init; }
    [Range(1, int.MaxValue)] public int Version { get; init; }
    [Required] public required string Requester { get; init; }
    [Range(0, 60)] public int ApplyDecisionDelaySeconds { get; init; }

    /// <summary>Ties this plan's dashboard entries, traces and logs together. Generated when omitted.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>Simulator settings, separate from policy configuration. Replaces one client/account's demo role assignments.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveDemoIdentitiesRequest
{
    [Required] public required string ClientId { get; init; }
    public string? AccountId { get; init; }
    [Required] public required Dictionary<string, string> Assignments { get; init; }
}
