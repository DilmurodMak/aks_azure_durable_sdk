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
}

/// <summary>A pending approval available to the demo console.</summary>
public sealed record WaitingApprovalResponse(
    string InstanceId, string PlanId, int Version, string Requester, string Approver, DateTimeOffset CreatedAt,
    ApprovalProgress? Progress = null, string? ParentInstanceId = null, int? PolicyVersion = null, string? Role = null);

/// <summary>A workflow available for viewing its complete pinned approval plan.</summary>
public sealed record WorkflowSummaryResponse(
    string InstanceId, string PlanId, int Version, string RuntimeStatus, int PolicyVersion, DateTimeOffset CreatedAt);

/// <summary>A planned reviewer step, including steps that have not yet been started by the worker.</summary>
public sealed record WorkflowApprovalResponse(
    string InstanceId, string ParentInstanceId, string PlanId, int Version, string Requester, string Approver,
    string Role, int PolicyVersion, string Rank, string State, bool CanDecide, bool CanView,
    string? BlockedReason, ApprovalProgress Progress);

/// <summary>Identifies the actual backend selected when this process started.</summary>
public sealed record ConsoleEnvironmentResponse(
    string Environment, string Endpoint, string TaskHub, string DashboardUrl, string Warning);

/// <summary>Save a new immutable policy version; stages run in order, roles in one stage run in parallel.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SavePolicyRequest
{
    [Required] public required string Id { get; init; }
    [Required] public required string ClientId { get; init; }
    public string? AccountId { get; init; }
    [Required] public required string WorkflowCode { get; init; }
    [Range(0, int.MaxValue - 1)] public int BaseVersion { get; init; }
    [Required] public required PolicyStage[] Stages { get; init; }
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
}

/// <summary>Simulator settings, separate from policy configuration. Replaces one client/account's demo role assignments.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveDemoIdentitiesRequest
{
    [Required] public required string ClientId { get; init; }
    public string? AccountId { get; init; }
    [Required] public required Dictionary<string, string> Assignments { get; init; }
}
