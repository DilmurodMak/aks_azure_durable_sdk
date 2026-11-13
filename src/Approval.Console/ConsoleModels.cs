using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Approval.Contracts;

namespace Approval.Console;

/// <summary>Starts a spike approval. Actor names are demonstration inputs, not authenticated identities.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateApprovalRequest
{
    [Required] public required string InstanceId { get; init; }
    [Required] public required string PlanId { get; init; }
    [Range(1, int.MaxValue)] public int Version { get; init; }
    [Required] public required string Requester { get; init; }
    [Required] public required string Approver { get; init; }
    [Range(0.001, 1440)] public double TimeoutMinutes { get; init; } = 15;
    [Range(0, 60)] public int ApplyDecisionDelaySeconds { get; init; }
}

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
    string InstanceId, string PlanId, int Version, string Requester, string Approver, DateTimeOffset CreatedAt);

/// <summary>Identifies the actual backend selected when this process started.</summary>
public sealed record ConsoleEnvironmentResponse(
    string Environment, string Endpoint, string TaskHub, string DashboardUrl, string Warning);
