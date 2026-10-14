using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Clippboard.Core;
using Clippboard.App.Platform;

internal static partial class Program
{
    private static async Task CheckEncryptedPreviewsAsync(string root)
    {
        var directory = Path.Combine(root, "derived-images");
        var calls = 0; byte[]? decodedSource = null;
        byte[] Generate(byte[] source) { ++calls; decodedSource = source; return [91, 92, 93]; }
        var limits = new HistoryLimits(5, 100000, 30, 20000);
        var store = new HistoryStore(directory, new WindowsProtector(), limits, Generate);
        await store.InitializeAsync();
        var original = new Clip { Kind = ClipKind.Image, ImagePng = [1, 2, 3, 4, 5] };
        await store.AddAsync(original);
        Check((await store.GetPreviewAsync(original.Id))!.SequenceEqual(new byte[] {91, 92, 93})
            && decodedSource!.All(x => x == 0), "derived image returned and temporary original cleared");
        var file = Path.Combine(directory, original.Id + ".thumb");
        Check(!File.ReadAllBytes(file).SequenceEqual(new byte[] {91, 92, 93}), "thumbnail stored with Windows user encryption");
        await store.GetPreviewAsync(original.Id);
        Check(calls == 1, "repeated preview fetch avoids full original read and generation");
        await store.SetPinnedAsync(original.Id, true);
        await store.GetPreviewAsync(original.Id);
        Check(calls == 1, "pinning does not invalidate content-identical preview");
        var restarted = new HistoryStore(directory, new WindowsProtector(), limits, Generate);
        await restarted.InitializeAsync(); await restarted.GetPreviewAsync(original.Id);
        Check(calls == 1, "encrypted derived previews survive process restart");
        await File.WriteAllBytesAsync(file, [0, 1]);
        await restarted.GetPreviewAsync(original.Id);
        Check(calls == 2 && (await restarted.GetAsync(original.Id))!.ImagePng!.SequenceEqual(original.ImagePng!),
            "corrupt preview rebuilds without modifying original image");
        await restarted.DeleteAsync(original.Id);
        Check(!File.Exists(file) && await restarted.GetPreviewAsync(original.Id) is null, "deleting original also removes its derived image");
        await File.WriteAllBytesAsync(file, [0, 1]); await restarted.InitializeAsync();
        Check(!File.Exists(file), "startup removes orphan preview files");

        var raceDirectory = Path.Combine(root, "derived-race");
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var race = new HistoryStore(raceDirectory, new WindowsProtector(), limits,
            _ => { started.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return [5, 6]; });
        await race.InitializeAsync(); await race.AddAsync(original);
        var pending = Task.Run(() => race.GetPreviewAsync(original.Id));
        Check(started.Wait(TimeSpan.FromSeconds(3)), "preview generator begins outside storage lock");
        try { await race.DeleteAsync(original.Id); } finally { release.Set(); }
        Check(await pending is null && Directory.GetFiles(raceDirectory, "*.thumb").Length == 0,
            "delete during preview generation cannot recreate expired history or orphan cache");

        var tinyDirectory = Path.Combine(root, "derived-budget");
        var tiny = new HistoryStore(tinyDirectory, new WindowsProtector(), limits with { MaxBytes = 2000 }, Generate);
        await tiny.InitializeAsync(); await tiny.AddAsync(original);
        Check(await tiny.GetPreviewAsync(original.Id) is not null && Directory.GetFiles(tinyDirectory, "*.thumb").Length == 0,
            "insufficient cache budget still permits viewing without writing derived files");
        await tiny.ClearAsync();
        Check(Directory.GetFiles(tinyDirectory).Length == 0, "clear removes payloads, metadata and derived images");
        var boundedDirectory = Path.Combine(root, "derived-invalid");
        var oversized = new byte[HistoryStore.MaxPreviewBytes + 1]; oversized[0] = 77;
        var bounded = new HistoryStore(boundedDirectory, new WindowsProtector(), limits, _ => oversized);
        await bounded.InitializeAsync(); await bounded.AddAsync(original); var rejected = false;
        try { await bounded.GetPreviewAsync(original.Id); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && oversized.All(x => x == 0) && Directory.GetFiles(boundedDirectory, "*.thumb").Length == 0,
            "oversized derived output rejected and wiped before cache allocation");
    }
}
