using System.IO;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

public class SnapshotStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SnapshotStore _store;

    public SnapshotStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"UE5DumpSnapTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new SnapshotStore(new MockPlatformService(_tempDir));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* WAL files may linger briefly; best effort */ }
    }

    private static SnapshotCapturedObject MakeObject(int index, params (string name, string type, string hex)[] fields)
    {
        var o = new SnapshotCapturedObject
        {
            Index = index,
            Addr = $"0x{index:X}",
            Name = $"BP_Player_C_{index}",
            ClassName = "BP_Player_C",
            OuterClassName = "World",
            Path = $"/Game/Map.Map:PersistentLevel.BP_Player_C_{index}",
        };
        foreach (var (name, type, hex) in fields)
            o.Fields.Add(new SnapshotCapturedField { Name = name, Type = type, Hex = hex, Offset = 0 });
        return o;
    }

    [Fact]
    public async Task CreateWriteListDelete_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var meta = new SnapshotMeta
        {
            Label = "first", CapturedAt = "2026-06-02T00:00:00Z",
            PeHash = "ABCD", GameSessionId = "ABCD-1", UeVersion = 504, Scope = "NumericNoByte",
        };
        long id = await _store.CreateSnapshotAsync(meta, ct);
        Assert.True(id > 0);

        int rows = await _store.WriteChunkAsync(id, new[]
        {
            MakeObject(1, ("Health", "FloatProperty", "0000803F"), ("Ammo", "IntProperty", "1E000000")),
            MakeObject(2, ("Health", "FloatProperty", "0000003F")),
        }, ct);
        Assert.Equal(3, rows);

        await _store.FinalizeSnapshotAsync(id, objectCount: 2, fieldCount: rows, ct);

        var list = await _store.ListSnapshotsAsync(ct);
        var saved = Assert.Single(list);
        Assert.Equal(id, saved.Id);
        Assert.Equal("first", saved.Label);
        Assert.Equal("ABCD", saved.PeHash);
        Assert.Equal(504, saved.UeVersion);
        Assert.Equal(2, saved.ObjectCount);
        Assert.Equal(3, saved.FieldCount);

        await _store.DeleteSnapshotAsync(id, ct);
        Assert.Empty(await _store.ListSnapshotsAsync(ct));
    }

    private async Task<long> SeedSnapshotAsync(string label, int idx, CancellationToken ct)
    {
        long id = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = label }, ct);
        await _store.WriteChunkAsync(id, new[] { MakeObject(idx, ("X", "IntProperty", "01000000")) }, ct);
        await _store.FinalizeSnapshotAsync(id, 1, 1, ct);
        return id;
    }

    [Fact]
    public async Task EnforceQuota_DropsOldestKeepsNewest()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSnapshotAsync("a", 1, ct);
        await SeedSnapshotAsync("b", 2, ct);
        await SeedSnapshotAsync("c", 3, ct);

        // A 1-byte quota evicts everything but the newest (always kept).
        int dropped = await _store.EnforceQuotaAsync(1, ct);
        Assert.Equal(2, dropped);
        Assert.Equal("c", Assert.Single(await _store.ListSnapshotsAsync(ct)).Label);
    }

    [Fact]
    public async Task EnforceQuota_UnlimitedIsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSnapshotAsync("a", 1, ct);
        Assert.Equal(0, await _store.EnforceQuotaAsync(0, ct));   // 0 = unlimited
        Assert.Single(await _store.ListSnapshotsAsync(ct));
    }

    [Fact]
    public async Task GetUsage_ReportsSizeAndCount()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSnapshotAsync("a", 1, ct);
        var u = await _store.GetUsageAsync(ct);
        Assert.Equal(1, u.SnapshotCount);
        Assert.True(u.GameDbBytes > 0);
        Assert.True(u.AllGamesBytes >= u.GameDbBytes);

        // List populates a positive size estimate for snapshots with fields.
        Assert.True(Assert.Single(await _store.ListSnapshotsAsync(ct)).EstBytes > 0);
    }

    [Fact]
    public async Task WriteChunk_EmptyIsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "e" }, ct);
        Assert.Equal(0, await _store.WriteChunkAsync(id, Array.Empty<SnapshotCapturedObject>(), ct));
    }

    [Fact]
    public async Task PerGameDb_IsolatesSnapshotsByPeHash()
    {
        var ct = TestContext.Current.CancellationToken;

        _store.SetActiveGame("GAMEA");
        var pathA = _store.DatabasePath;
        await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "a", PeHash = "GAMEA" }, ct);

        _store.SetActiveGame("GAMEB");
        var pathB = _store.DatabasePath;
        await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "b", PeHash = "GAMEB" }, ct);

        Assert.NotEqual(pathA, pathB);
        Assert.Contains("GAMEA", pathA);
        Assert.Contains("GAMEB", pathB);

        // Each game sees only its own snapshot.
        _store.SetActiveGame("GAMEA");
        var listA = await _store.ListSnapshotsAsync(ct);
        Assert.Equal("a", Assert.Single(listA).Label);

        _store.SetActiveGame("GAMEB");
        var listB = await _store.ListSnapshotsAsync(ct);
        Assert.Equal("b", Assert.Single(listB).Label);
    }

    [Fact]
    public void SetActiveGame_SanitizesPeHashIntoFilename()
    {
        _store.SetActiveGame("../../evil\\x00");
        // Only ASCII alphanumerics survive (drops . / \) -> no path traversal.
        // Raw chars kept: e v i l x 0 0  ->  "evilx00".
        Assert.DoesNotContain("..", _store.DatabasePath);
        Assert.EndsWith("snapshots.evilx00.db", _store.DatabasePath);
    }

    [Fact]
    public async Task MultipleSnapshots_ListedNewestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        long a = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "a" }, ct);
        long b = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "b" }, ct);
        var list = await _store.ListSnapshotsAsync(ct);
        Assert.Equal(2, list.Count);
        Assert.Equal(b, list[0].Id);  // newest (higher id) first
        Assert.Equal(a, list[1].Id);
    }
}
