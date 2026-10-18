using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Clippboard.App.Services;
using Clippboard.Core;

internal static partial class Program
{
    private static async Task CheckCatalogAsync()
    {
        var first = new ClipSummary("first", ClipKind.Text, DateTimeOffset.UnixEpoch, false, "first note", "first:hash", 100, Labels: ["work"]);
        var second = first with { Id = "second", Preview = "second note", Fingerprint = "second:hash" };
        var sender = new CatalogSender(); var replica = new CatalogReplica();
        var full = sender.Create([first, second], 0); var initial = replica.Apply(full);
        Check(full.Full && initial.Count == 2 && initial[0].Id == "first", "catalog starts with a bounded full snapshot in exact store order");
        var clones = JsonSerializer.Deserialize<ClipSummary[]>(JsonSerializer.Serialize(new[] { first, second }))!;
        var unchanged = sender.Create(clones, replica.Version);
        Check(!unchanged.Full && unchanged.Entries.Length == 0 && unchanged.Order is null
            && ReferenceEquals(replica.Apply(unchanged), initial), "unchanged deserialized catalog sends no entries and reuses its read-only snapshot");
        var pinned = second with { Pinned = true, Title = "Pinned", Labels = ["updated"], HasRichText = true };
        var update = sender.Create([pinned, first], replica.Version); var updated = replica.Apply(update);
        Check(!update.Full && update.Entries.Length == 1 && update.Entries[0].Id == "second"
            && updated[0].ContentEquals(pinned) && ReferenceEquals(updated[1], initial[0]), "catalog transmits one changed entry and retains unchanged metadata objects");
        var orderOnly = sender.Create([first, pinned], replica.Version);
        Check(orderOnly.Entries.Length == 0 && replica.Apply(orderOnly).Select(x => x.Id).SequenceEqual(["first", "second"]), "order-only changes preserve exact ordering including timestamp ties");
        var third = first with { Id = "third" };
        var replacement = sender.Create([third, pinned], replica.Version); var replaced = replica.Apply(replacement);
        Check(replacement.Removed.SequenceEqual(["first"]) && replacement.Entries.Length == 1 && replaced[0].Id == "third",
            "catalog atomically adds copied notes and removes deleted or expired references");
        // Simulate a reply lost after the sender advanced but before application.
        _ = sender.Create([third with { Preview = "missed" }], replica.Version);
        var recovery = sender.Create([third with { Preview = "current" }], replica.Version);
        Check(recovery.Full && replica.Apply(recovery).Single().Preview == "current", "lost reply repairs with a full snapshot using the client's acknowledged revision");
        var stableVersion = replica.Version; var stable = replica.Apply(sender.Create([third with { Preview = "current" }], stableVersion));
        CatalogUpdate[] malformed = [
            new(stableVersion - 1, stableVersion + 1, false, [], [], ["third"]),
            new(stableVersion, stableVersion + 2, false, [], [], ["third"]),
            new(stableVersion, stableVersion + 1, false, [third, third], [], ["third"]),
            new(stableVersion, stableVersion + 1, false, [], ["missing"], []),
            new(stableVersion, stableVersion + 1, false, [], [], ["missing"]),
            new(stableVersion, stableVersion + 1, false, [], [], null),
            new(stableVersion, stableVersion + 1, true, [third], ["third"]),
            new(stableVersion, stableVersion + 1, true, Enumerable.Repeat(third, AppSettings.MaximumEntries + 1).ToArray(), [])
        ];
        foreach (var bad in malformed)
        {
            var rejected = false; try { replica.Apply(bad); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && replica.Version == stableVersion && ReferenceEquals(replica.Apply(new(stableVersion, stableVersion, false, [], [])), stable),
                "malformed catalog cannot partially change data or revision");
        }
        var cleared = sender.Create([], replica.Version);
        Check(!cleared.Full && replica.Apply(cleared).Count == 0, "clear and empty history remove all cached metadata");
        sender = new CatalogSender();
        Check(replica.Apply(sender.Create([first], replica.Version)).Single().Id == "first", "new sender session replaces an older replica revision safely");
        replica.Reset(); Check(replica.Version == 0, "discarding a failed catalog requests a fresh snapshot");
        var largeRows = Enumerable.Range(0, AppSettings.MaximumEntries).Select(index => first with
            { Id = index.ToString("x32"), Preview = new string('p', 320), Labels = ["work", "fixture"] }).ToArray();
        var largeSender = new CatalogSender(); var largeInitial = largeSender.Create(largeRows, 0);
        var fullWire = JsonSerializer.SerializeToUtf8Bytes(largeInitial);
        largeRows = largeRows.ToArray(); largeRows[0] = largeRows[0] with { Preview = "changed fixture" };
        var smallUpdate = largeSender.Create(largeRows, largeInitial.Version); var deltaWire = JsonSerializer.SerializeToUtf8Bytes(smallUpdate);
        Check(smallUpdate.Entries.Length == 1 && deltaWire.Length < fullWire.Length / 10,
            "one-entry change in the maximum catalog avoids resending unchanged preview text and labels");
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(fullWire); System.Security.Cryptography.CryptographicOperations.ZeroMemory(deltaWire);
        var rows = new[] { first, second }; var pipeSender = new CatalogSender();
        using var server = new OverlayServer(request => Task.FromResult(request.Command == "catalog"
            ? new OverlayReply(Catalog: pipeSender.Create(rows, request.CatalogVersion)) : new OverlayReply(Error: "Unexpected command")));
        var running = server.RunAsync();
        using var client = new OverlayClient(server.Name);
        var overPipe = await client.ListAsync();
        var simultaneous = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.ListAsync())).WaitAsync(TimeSpan.FromSeconds(5));
        Check(simultaneous.All(snapshot => ReferenceEquals(snapshot, overPipe)), "concurrent catalog calls serialize acknowledgment and reuse the same snapshot over real pipes");
        rows = [second with { Pinned = true }];
        var changedPipe = await client.ListAsync();
        Check(changedPipe.Count == 1 && changedPipe[0].Id == "second" && changedPipe[0].Pinned && overPipe.Count == 2,
            "incremental pipe updates preserve earlier immutable snapshots while applying deletion and pinning");
        server.Dispose(); await running.WaitAsync(TimeSpan.FromSeconds(3)); await server.Shutdown;
    }
}
