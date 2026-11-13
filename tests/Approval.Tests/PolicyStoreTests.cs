using System.Text.Json;
using Approval.Console;
using Approval.Contracts;

namespace Approval.Tests;

public sealed class PolicyStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"approval-policy-test-{Guid.NewGuid():N}");
    private PolicyStore Store => new(Path.Combine(directory, "registry.json"));

    [Fact]
    public async Task SaveActivatesNewSnapshotsOnlyAndSelectionCanRestoreAnEarlierVersion()
    {
        PolicyStore store = Store;
        ApprovalPolicy first = await store.SaveAsync(Draft(), default);
        ApprovalPolicy running = await store.ResolveAsync("client", null, "Gate2", default);
        ApprovalPolicy edited = await store.SaveAsync(Draft() with
        {
            BaseVersion = 1,
            Stages = [new("parallel", ["Finance", "Legal"])]
        }, default);
        Assert.Equal(2, (await store.ResolveAsync("client", null, "Gate2", default)).Version);
        ApprovalPolicy next = await Store.ResolveAsync("client", null, "Gate2", default);

        Assert.Equal(1, running.Version);
        Assert.Equal(["Finance"], running.Stages[0].Roles);
        Assert.Equal(2, next.Version);
        Assert.Equal(["Finance", "Legal"], next.Stages[0].Roles);
        Assert.Equal(2, (await Store.ListAsync(default)).Policies.Length);
        await store.ActivateAsync(first.Id, first.Version, default);
        Assert.Equal(1, (await Store.ResolveAsync("client", null, "Gate2", default)).Version);
        Assert.Equal(2, next.Version);
    }

    [Fact]
    public async Task AccountThenClientThenAgencyDefaultSelectionIsExplicit()
    {
        PolicyStore store = Store;
        foreach (SavePolicyRequest draft in new[]
        {
            Draft() with { Id = "default-policy", ClientId = "agency-default" },
            Draft() with { Id = "client-policy" },
            Draft() with { Id = "account-policy", AccountId = "account" }
        })
        {
            ApprovalPolicy saved = await store.SaveAsync(draft, default);
            await store.ActivateAsync(saved.Id, 1, default);
        }
        Assert.Equal("account-policy", (await store.ResolveAsync("client", "account", "Gate2", default)).Id);
        Assert.Equal("client-policy", (await store.ResolveAsync("client", "other", "Gate2", default)).Id);
        Assert.Equal("default-policy", (await store.ResolveAsync("other", null, "Gate2", default)).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ResolveAsync("client", null, "Unknown", default));
    }

    [Fact]
    public async Task ConflictingEditsAndInvalidStagesFailWithoutWritingANewVersion()
    {
        PolicyStore store = Store;
        await store.SaveAsync(Draft(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Draft(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Draft() with
        {
            BaseVersion = 1,
            Stages = []
        }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Draft() with
        {
            BaseVersion = 1,
            ClientId = "other"
        }, default));
        Assert.Single((await store.ListAsync(default)).Policies);
        Assert.Equal(1, (await store.ResolveAsync("client", null, "Gate2", default)).Version);
    }

    [Fact]
    public async Task SaveIsImmediatelyActiveAndCorruptStorageIsNotSilentlyReset()
    {
        PolicyStore store = Store;
        await store.SaveAsync(Draft(), default);
        Assert.Equal(1, (await store.ResolveAsync("client", null, "Gate2", default)).Version);
        await File.WriteAllTextAsync(Path.Combine(directory, "registry.json"), "{bad json");
        await Assert.ThrowsAsync<JsonException>(() => store.ListAsync(default));
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
        Assert.Equal("other-policy", (await Store.ResolveAsync("client", null, "Gate2", default)).Id);
        Assert.Equal("account-policy", (await Store.ResolveAsync("client", "account", "Gate2", default)).Id);
    }

    [Fact]
    public async Task ConcurrentEditsCannotOverwriteAnExistingVersion()
    {
        PolicyStore store = Store;
        await store.SaveAsync(Draft(), default);
        SavePolicyRequest edit = Draft() with { BaseVersion = 1, Stages = [new("updated", ["Finance"])] };
        Task<ApprovalPolicy> first = store.SaveAsync(edit, default);
        Task<ApprovalPolicy> second = store.SaveAsync(edit, default);
        await first;
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(2, (await store.ListAsync(default)).Policies.Length);
    }

    [Fact]
    public async Task UnchangedSavePreservesVersionTimestampAndFileBytes()
    {
        PolicyStore store = Store;
        SavePolicyRequest draft = Draft();
        ApprovalPolicy saved = await store.SaveAsync(draft, default);
        string file = Path.Combine(directory, "registry.json");
        byte[] before = await File.ReadAllBytesAsync(file);
        ApprovalPolicy unchanged = await store.SaveAsync(draft with
        {
            BaseVersion = 1,
            Stages = [new("finance", ["Finance"])]
        }, default);

        Assert.Equal(saved.Version, unchanged.Version);
        Assert.Equal(saved.SavedAt, unchanged.SavedAt);
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        Assert.Single((await store.ListAsync(default)).Policies);
    }

    [Fact]
    public async Task ReviewerAndStageChangesStillCreateVersions()
    {
        PolicyStore store = Store;
        await store.SaveAsync(Draft(), default);
        ApprovalPolicy changed = await store.SaveAsync(Draft() with
        {
            BaseVersion = 1,
            Stages = [new("finance", ["Finance", "Legal"])]
        }, default);
        Assert.Equal(2, changed.Version);
        Assert.Equal(2, (await store.ResolveAsync("client", null, "Gate2", default)).Version);
    }

    private static SavePolicyRequest Draft() => new()
    {
        Id = "policy",
        ClientId = "client",
        WorkflowCode = "Gate2",
        Stages = [new("finance", ["Finance"])]
    };

    public void Dispose()
    {
        string file = Path.Combine(directory, "registry.json");
        if (File.Exists(file)) File.Delete(file);
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
}
