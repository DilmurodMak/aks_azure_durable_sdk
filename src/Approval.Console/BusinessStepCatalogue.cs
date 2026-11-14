namespace Approval.Console;

/// <summary>A business workflow from the platform analysis that reuses Approval &amp; sign-off.</summary>
public sealed record BusinessStep(string Code, string Name, string PhaseCode, string PhaseName);

/// <summary>
/// Spike-only, hard-coded list of the business workflows in the analysis whose template includes
/// Approval &amp; sign-off. A production system would read this from the workflow catalogue.
/// </summary>
public static class BusinessStepCatalogue
{
    public static IReadOnlyList<BusinessStep> All { get; } =
    [
        new("F02", "MSA / SOW scope capture & approval", "P1", "P1 - Foundation & Governance"),
        new("F03", "Contract & commercial terms approval", "P1", "P1 - Foundation & Governance"),
        new("F04", "Staff plan approval & changes", "P1", "P1 - Foundation & Governance"),
        new("F05", "Financial plan, forecast & true-up approval", "P1", "P1 - Foundation & Governance"),
        new("F07", "Cost, T&E, tool & AI-usage approval", "P1", "P1 - Foundation & Governance"),
        new("F11", "Rates & benchmark approval", "P1", "P1 - Foundation & Governance"),
        new("F12", "Buy guidelines & policy approval", "P1", "P1 - Foundation & Governance"),
        new("F14", "Data contract approval (vendor)", "P1", "P1 - Foundation & Governance"),
        new("F21", "Report / dataset go-live approval", "P1", "P1 - Foundation & Governance"),
        new("F25", "Data service governance & criticality approval", "P1", "P1 - Foundation & Governance"),
        new("F26", "Feed configuration lifecycle", "P1", "P1 - Foundation & Governance"),
        new("S01", "Brief intake, approval & lock", "P2", "P2 - Strategy & Planning"),
        new("S04", "Strategy step confirm / lock / approve", "P2", "P2 - Strategy & Planning"),
        new("S05", "Strategic plan & Gate 1 approval", "P2", "P2 - Strategy & Planning"),
        new("B03", "Proposal evaluation & award", "P3", "P3 - Sourcing & Buy Authorization"),
        new("B04", "Negotiation & terms approval", "P3", "P3 - Sourcing & Buy Authorization"),
        new("B07", "Tactical plan & Gate 2 approval", "P3", "P3 - Sourcing & Buy Authorization"),
        new("B08", "Fees / non-working cost approval", "P3", "P3 - Sourcing & Buy Authorization"),
        new("B09", "Buying audiences build & approval", "P3", "P3 - Sourcing & Buy Authorization"),
        new("E01", "Client buy authorization", "P4", "P4 - Execution & Trafficking"),
        new("E06", "Station creative clearance (TV)", "P4", "P4 - Execution & Trafficking"),
        new("E09", "Pre-launch QA go / no-go", "P4", "P4 - Execution & Trafficking"),
        new("I03", "Optimization recommendation & approval", "P5", "P5 - In-flight Management"),
        new("I07", "Client reporting review & publish", "P5", "P5 - In-flight Management"),
        new("I09", "AI insight review & publication", "P5", "P5 - In-flight Management"),
        new("R02", "Client billing & invoice approval", "P6", "P6 - Reconciliation & Close"),
        new("R06", "Close & archive sign-off", "P6", "P6 - Reconciliation & Close"),
        new("R07", "Post-buy analysis & learnings approval", "P6", "P6 - Reconciliation & Close"),
        new("X03", "Release & platform change management", "X", "Cross-cutting platform"),
        new("X05", "AI capability / agent lifecycle approval", "X", "Cross-cutting platform"),
        new("X07", "Report / dashboard publish, version & retire", "X", "Cross-cutting platform"),
        new("X09", "Audit evidence & compliance review", "X", "Cross-cutting platform"),
        new("X14", "Security & control exception approval", "X", "Cross-cutting platform"),
        new("X16", "Data period lock, certification & restatement", "X", "Cross-cutting platform")
    ];

    public static bool Contains(string? code) => All.Any(step => step.Code == code);
}
