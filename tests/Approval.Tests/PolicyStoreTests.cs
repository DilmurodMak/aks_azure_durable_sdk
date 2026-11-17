using System.Text.Json;
using Approval.Console;
using Approval.Contracts;

namespace Approval.Tests;

public sealed class PolicyStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"approval-policy-test-{Guid.NewGuid():N}");
    private string RegistryPath => Path.Combine(directory, "registry.json");
    private PolicyStore Store => new(RegistryPath);

    [Theory]
    [InlineData("4999.99", false)]
    [InlineData("5000", false)]
    [InlineData("5000.01", true)]
    [InlineData("7500", true)]
    public void BudgetUsesExactDecimalStrictGreaterThanAndRetainsSkippedReasons(string amount, bool included)
    {
        decimal budget = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        PolicyStage[] stages = [new("Internal", ["Lead"]), new("Finance", ["Finance"], BudgetGreaterThan: 5000)];
        BudgetRouting routing = BudgetRouting.Evaluate(budget, stages);
        Assert.True(routing.Stages[0].Included);
        Assert.Equal(included, routing.Stages[1].Included);
        Assert.Equal(budget, routing.BudgetAmount);
        Assert.Equal("USD", routing.Currency);
        Assert.Contains(included ? ">" : "<=", routing.Stages[1].Reason);
        Assert.Equal(5000, routing.Stages[1].Stage.BudgetGreaterThan);
        BudgetRouting roundTrip = JsonSerializer.Deserialize<BudgetRouting>(JsonSerializer.Serialize(routing))!;
        Assert.Equal(budget, roundTrip.BudgetAmount);
        Assert.Equal(routing.Stages[1].Reason, roundTrip.Stages[1].Reason);
        Assert.Equal(5000, roundTrip.Stages[1].Stage.BudgetGreaterThan);
    }

    [Fact]
    public async Task ThresholdOnlyEditsCreateVersionsAndKeepAnExistingRoutePinned()
    {
        PolicyStore store = Store;
        SavePolicyRequest draft = Draft() with
        {
            DefaultStages = [new("Internal", ["Lead"]), new("Finance", ["Finance"], BudgetGreaterThan: 5000)]
        };
        await store.SaveAsync(draft, default);
        PolicyMatch first = await store.ResolveAsync("client", null, "B07", default);
        BudgetRouting original = BudgetRouting.Evaluate(7500, first.Stages);
        ApprovalPolicy second = await store.SaveAsync(draft with
        {
            BaseVersion = 1,
            DefaultStages = [new("Internal", ["Lead"]), new("Finance", ["Finance"], BudgetGreaterThan: 10000)]
        }, default);
        Assert.Equal(2, second.Version);
        BudgetRouting next = BudgetRouting.Evaluate(7500, (await store.ResolveAsync("client", null, "B07", default)).Stages);
        Assert.True(original.Stages[1].Included);
        Assert.False(next.Stages[1].Included);
        Assert.Equal(1, first.Policy.Version);
        Assert.Equal(2, (await store.SaveAsync(draft with { BaseVersion = 2, DefaultStages = second.DefaultStages }, default)).Version);
        Assert.Contains("budgetGreaterThan", await File.ReadAllTextAsync(RegistryPath));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("0.001")]
    [InlineData("1000000000000")]
    public async Task InvalidThresholdsAndBudgetsAreRejectedWithoutSaving(string amount)
    {
        decimal value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentException>(() => BudgetRouting.Evaluate(value, [new("Internal", ["Lead"])]));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SaveAsync(Draft() with
        {
            DefaultStages = [new("Internal", ["Lead"]), new("Finance", ["Finance"], BudgetGreaterThan: value)]
        }, default));
    }

    [Fact]
    public async Task APolicyCannotMakeEveryStageConditional()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SaveAsync(Draft() with
        {
            DefaultStages = [new("Finance", ["Finance"], BudgetGreaterThan: 5000)]
        }, default));
    }

    [Fact]
    public async Task SaveActivatesNewSnapshotsOnlyAndSelectionCanRestoreAnEarlierVersion()
    {
        PolicyStore store = Store;
        ApprovalPolicy first = await store.SaveAsync(Draft(), default);
        PolicyMatch running = await store.ResolveAsync("client", null, "B07", default);
        await store.SaveAsync(Draft() with
        {
            BaseVersion = 1,
            DefaultStages = [new("parallel", ["Finance", "Legal"])]
        }, default);
        PolicyMatch next = await Store.ResolveAsync("client", null, "B07", default);

        Assert.Equal(1, running.Policy.Version);
        Assert.Equal(["Finance"], running.Stages[0].Roles);
        Assert.Equal(2, next.Policy.Version);
        Assert.Equal(["Finance", "Legal"], next.Stages[0].Roles);
        Assert.Equal(2, (await Store.ListAsync(default)).Policies.Length);
        await store.ActivateAsync(first.Id, first.Version, default);
        Assert.Equal(1, (await Store.ResolveAsync("client", null, "B07", default)).Policy.Version);
    }

    [Fact]
    public async Task OneDefaultCoversEveryStepAndAnOverrideReplacesItForOneStep()
    {
        PolicyStore store = Store;
        await store.SaveAsync(Draft() with
        {
            Overrides = [new("B07", [new("lead", ["Lead"]), new("client", ["Client"])])]
        }, default);

        PolicyMatch gate2 = await store.ResolveAsync("client", null, "B07", default);
        PolicyMatch other = await store.ResolveAsync("client", null, "B08", default);
        PolicyMatch another = await store.ResolveAsync("client", null, "F02", default);

        Assert.Equal("client override", gate2.Source);
        Assert.Equal(["lead", "client"], gate2.Stages.Select(stage => stage.Name));
        Assert.Equal("client default", other.Source);
        Assert.Equal("client default", another.Source);
        Assert.Equal(["finance"], other.Stages.Select(stage => stage.Name));
        Assert.Equal("B08", other.WorkflowCode);
        Assert.Single((await store.ListAsync(default)).Policies);
    }

    [Fact]
    public async Task NarrowerOwnerWinsThenStepOverrideBeatsDefaultWithinOneOwner()
    {
        PolicyStore store = Store;
        await store.SaveAsync(Draft() with { Id = "agency", ClientId = "agency-default" }, default);
        await store.SaveAsync(Draft() with
        {
            Id = "client-set",
            DefaultStages = [new("client-default", ["Finance", "Legal"])],
            Overrides = [new("B07", [new("client-gate", ["Client"])])]
        }, default);
        await store.SaveAsync(new SavePolicyRequest
        {
            Id = "account-sparse",
            ClientId = "client",
            AccountId = "sparse",
            Overrides = [new("B07", [new("account-gate", ["Lead"])])]
        }, default);
        await store.SaveAsync(Draft() with
        {
            Id = "account-full",
            AccountId = "full",
            DefaultStages = [new("account-default", ["Lead"])]
        }, default);

        Assert.Equal("account override", (await store.ResolveAsync("client", "sparse", "B07", default)).Source);
        Assert.Equal("client default", (await store.ResolveAsync("client", "sparse", "B08", default)).Source);
        Assert.Equal("account default", (await store.ResolveAsync("client", "full", "B07", default)).Source);
        Assert.Equal("client override", (await store.ResolveAsync("client", "other", "B07", default)).Source);
        Assert.Equal("client override", (await store.ResolveAsync("client", null, "B07", default)).Source);
        Assert.Equal("client default", (await store.ResolveAsync("client", null, "B08", default)).Source);
        PolicyMatch agency = await store.ResolveAsync("other-client", null, "B07", default);
        Assert.Equal("agency default", agency.Source);
        Assert.Equal("agency", agency.Policy.Id);
    }

    [Fact]
    public async Task NoMatchIsAnExplicitErrorNotAGuessedChain()
    {
        PolicyStore store = Store;
        await store.SaveAsync(new SavePolicyRequest
        {
            Id = "only-override",
            ClientId = "client",
            Overrides = [new("B07", [new("gate", ["Finance"])])]
        }, default);
        Assert.Equal("client override", (await store.ResolveAsync("client", null, "B07", default)).Source);
        await Assert.ThrowsAsync<NoMatchingPolicyException>(() => store.ResolveAsync("client", null, "B08", default));
        await Assert.ThrowsAsync<NoMatchingPolicyException>(() => store.ResolveAsync("other", null, "B07", default));
    }

    [Fact]
    public async Task EditingOneStepOverrideCreatesOneSetVersionAndLeavesOtherStepsUnchanged()
    {
        PolicyStore store = Store;
        await store.SaveAsync(Draft() with { Overrides = [new("B07", [new("gate", ["Client"])])] }, default);
        PolicyMatch otherBefore = await store.ResolveAsync("client", null, "B08", default);
        ApprovalPolicy changed = await store.SaveAsync(Draft() with
        {
            BaseVersion = 1,
            Overrides = [new("B07", [new("gate", ["Client", "Legal"])])]
        }, default);
        PolicyMatch otherAfter = await store.ResolveAsync("client", null, "B08", default);

        Assert.Equal(2, changed.Version);
        Assert.Equal(2, otherAfter.Policy.Version);
        Assert.Equal(otherBefore.Stages.Select(stage => stage.Roles), otherAfter.Stages.Select(stage => stage.Roles));
        Assert.Equal(["Client", "Legal"], (await store.ResolveAsync("client", null, "B07", default)).Stages[0].Roles);
        Assert.Equal(2, (await store.ListAsync(default)).Policies.Length);
    }

    [Fact]
    public async Task ConflictingEditsAndInvalidSetsFailWithoutWritingANewVersion()
    {
        PolicyStore store = Store;
        await store.SaveAsync(Draft(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Draft(), default));
        foreach (SavePolicyRequest invalid in new[]
        {
            Draft() with { BaseVersion = 1, DefaultStages = [] },
            Draft() with { BaseVersion = 1, DefaultStages = null },
            Draft() with { BaseVersion = 1, ClientId = "other" },
            Draft() with { BaseVersion = 1, AccountId = "account" },
            Draft() with
            {
                BaseVersion = 1,
                Overrides = [new("B07", [new("a", ["Finance"])]), new("B07", [new("b", ["Finance"])])]
            },
            Draft() with { BaseVersion = 1, Overrides = [new("B07", [])] },
            Draft() with { BaseVersion = 1, Overrides = [new("bad code", [new("a", ["Finance"])])] }
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(invalid, default));
        }

        Assert.Single((await store.ListAsync(default)).Policies);
        Assert.Equal(1, (await store.ResolveAsync("client", null, "B07", default)).Policy.Version);
    }

    [Fact]
    public async Task SaveIsImmediatelyActiveAndCorruptStorageIsNotSilentlyReset()
    {
        PolicyStore store = Store;
        await store.SaveAsync(Draft(), default);
        Assert.Equal(1, (await store.ResolveAsync("client", null, "B07", default)).Policy.Version);
        await File.WriteAllTextAsync(RegistryPath, "{bad json");
        await Assert.ThrowsAnyAsync<JsonException>(() => store.ListAsync(default));
    }

    [Fact]
    public async Task SaveReplacesOnlyTheActiveSelectionInTheSameScope()
    {
        PolicyStore store = Store;
        await store.SaveAsync(Draft(), default);
        await store.SaveAsync(Draft() with { Id = "other-policy" }, default);
        await store.SaveAsync(Draft() with { Id = "account-policy", AccountId = "account" }, default);
        PolicyRegistry registry = await Store.ListAsync(default);
        Assert.Equal(3, registry.Policies.Length);
        Assert.Equal(2, registry.Active.Length);
        Assert.Equal("other-policy", (await Store.ResolveAsync("client", null, "B07", default)).Policy.Id);
        Assert.Equal("account-policy", (await Store.ResolveAsync("client", "account", "B07", default)).Policy.Id);
    }

    [Fact]
    public async Task ConcurrentEditsCannotOverwriteAnExistingVersion()
    {
        PolicyStore store = Store;
        await store.SaveAsync(Draft(), default);
        SavePolicyRequest edit = Draft() with { BaseVersion = 1, DefaultStages = [new("updated", ["Finance"])] };
        Task<ApprovalPolicy> first = store.SaveAsync(edit, default);
        Task<ApprovalPolicy> second = store.SaveAsync(edit, default);
        await first;
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(2, (await store.ListAsync(default)).Policies.Length);
    }

    [Fact]
    public async Task UnchangedSavePreservesVersionTimestampAndFileBytesEvenIfOverrideOrderDiffers()
    {
        PolicyStore store = Store;
        SavePolicyRequest draft = Draft() with
        {
            Overrides = [new("B08", [new("fees", ["Finance"])]), new("B07", [new("gate", ["Client"])])]
        };
        ApprovalPolicy saved = await store.SaveAsync(draft, default);
        byte[] before = await File.ReadAllBytesAsync(RegistryPath);
        ApprovalPolicy unchanged = await store.SaveAsync(draft with
        {
            BaseVersion = 1,
            Overrides = [new("B07", [new("gate", ["Client"])]), new("B08", [new("fees", ["Finance"])])]
        }, default);

        Assert.Equal(["B07", "B08"], saved.Overrides.Select(item => item.WorkflowCode));
        Assert.Equal(saved.Version, unchanged.Version);
        Assert.Equal(saved.SavedAt, unchanged.SavedAt);
        Assert.Equal(before, await File.ReadAllBytesAsync(RegistryPath));
        Assert.Single((await store.ListAsync(default)).Policies);
    }

    [Fact]
    public async Task ReviewerStageTimeoutAndOverrideChangesStillCreateVersions()
    {
        PolicyStore store = Store;
        await store.SaveAsync(Draft(), default);
        ApprovalPolicy reviewer = await store.SaveAsync(Draft() with
        {
            BaseVersion = 1,
            DefaultStages = [new("finance", ["Finance", "Legal"])]
        }, default);
        ApprovalPolicy timeout = await store.SaveAsync(Draft() with
        {
            BaseVersion = 2,
            DefaultStages = [new("finance", ["Finance", "Legal"], 30)]
        }, default);
        ApprovalPolicy added = await store.SaveAsync(Draft() with
        {
            BaseVersion = 3,
            DefaultStages = [new("finance", ["Finance", "Legal"], 30)],
            Overrides = [new("B07", [new("gate", ["Client"])])]
        }, default);
        Assert.Equal([2, 3, 4], [reviewer.Version, timeout.Version, added.Version]);
    }

    private static SavePolicyRequest Draft() => new()
    {
        Id = "policy",
        ClientId = "client",
        DefaultStages = [new("finance", ["Finance"])]
    };

    public void Dispose()
    {
        if (File.Exists(RegistryPath)) File.Delete(RegistryPath);
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
}
