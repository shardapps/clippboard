using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Clippboard.Core;
using Clippboard.App.Platform;

internal static partial class Program
{
    private sealed class StorageProtector : IContentProtector
    {
        private readonly WindowsProtector _inner = new();
        internal int LargestProtected, UnprotectCalls;
        internal byte[]? LastPlaintext;
        public byte[] Protect(byte[] data) { LargestProtected = Math.Max(LargestProtected, data.Length); return _inner.Protect(data); }
        public byte[] Unprotect(byte[] data) { ++UnprotectCalls; return LastPlaintext = _inner.Unprotect(data); }
    }
    private sealed class UnavailableSummaryProtector : IContentProtector
    {
        private readonly WindowsProtector _inner = new();
        public byte[] Protect(byte[] data) => data.AsSpan().StartsWith("{\"Summary\":"u8)
            ? throw new CryptographicException("Synthetic optional metadata failure") : _inner.Protect(data);
        public byte[] Unprotect(byte[] data) => _inner.Unprotect(data);
    }
    private static async Task CheckImageStorageAsync(string root)
    {
        var directory = Path.Combine(root, "binary-image-records");
        var protector = new StorageProtector(); var store = new HistoryStore(directory, protector); await store.InitializeAsync();
        var png = new byte[2 * 1024 * 1024]; new Random(31).NextBytes(png);
        var clip = new Clip { Kind = ClipKind.Image, ImagePng = png };
        Check(await store.AddAsync(clip), "binary image record accepted");
        var path = Path.Combine(directory, clip.Id + ".clip2");
        var stored = await File.ReadAllBytesAsync(path);
        Check(stored.AsSpan(0, 8).SequenceEqual("CLIPIMG2"u8) && protector.LargestProtected == png.Length,
            "raw image encryption avoids a larger Base64 plaintext buffer");
        var read = await store.GetAsync(clip.Id);
        Check(read!.ImagePng!.SequenceEqual(png) && png.Any(x => x != 0), "binary storage preserves exact caller-owned image bytes");
        CryptographicOperations.ZeroMemory(read.ImagePng);
        await store.SetPinnedAsync(clip.Id, true);
        Check(protector.LastPlaintext!.All(x => x == 0) && (await store.ListAsync()).Single().Pinned,
            "pin rewrite clears temporary decoded image after preserving payload");
        stored = await File.ReadAllBytesAsync(path);
        var header = stored[..16]; BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), int.MaxValue);
        await File.WriteAllBytesAsync(path, header); var calls = protector.UnprotectCalls; var rejected = false;
        try { await store.GetAsync(clip.Id); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && protector.UnprotectCalls == calls, "forged image lengths rejected before allocation/decryption");
        await File.WriteAllBytesAsync(path, "CLIPIMG2"u8.ToArray()); rejected = false;
        try { await store.GetAsync(clip.Id); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "truncated versioned image header rejected");
        await File.WriteAllBytesAsync(path, stored);
        var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(stored.AsSpan(8));
        var replacement = protector.Protect(Enumerable.Repeat((byte)42, png.Length).ToArray());
        Check(replacement.Length == stored.Length - metadataLength - 16, "synthetic corruption fixture keeps frame bounds unchanged");
        replacement.CopyTo(stored, metadataLength + 16); await File.WriteAllBytesAsync(path, stored); rejected = false;
        try { await store.GetAsync(clip.Id); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && protector.LastPlaintext!.All(x => x == 0), "authenticated image blocks cannot be swapped between metadata records; rejected plaintext cleared");

        var legacyDirectory = Path.Combine(root, "legacy-image-records"); Directory.CreateDirectory(legacyDirectory);
        var legacyPlain = JsonSerializer.SerializeToUtf8Bytes(clip); var legacyEncrypted = protector.Protect(legacyPlain);
        CryptographicOperations.ZeroMemory(legacyPlain);
        var legacyPath = Path.Combine(legacyDirectory, clip.Id + ".clip"); await File.WriteAllBytesAsync(legacyPath, legacyEncrypted);
        var legacy = new HistoryStore(legacyDirectory, protector); await legacy.InitializeAsync();
        var legacyRead = (await legacy.GetAsync(clip.Id))!;
        Check(legacyRead.ImagePng!.SequenceEqual(png), "existing encrypted JSON images remain readable without startup migration");
        CryptographicOperations.ZeroMemory(legacyRead.ImagePng);
        await legacy.SetPinnedAsync(clip.Id, true);
        var rewrittenPath = Path.ChangeExtension(legacyPath, ".clip2");
        var rewritten = await File.ReadAllBytesAsync(rewrittenPath);
        Check(rewritten.AsSpan(0, 8).SequenceEqual("CLIPIMG2"u8) && rewritten.Length < legacyEncrypted.Length,
            "rewritten legacy image adopts smaller binary storage without changing identity");
        Check(!File.Exists(legacyPath) && Directory.GetFiles(legacyDirectory, "*.clip").Length == 0,
            "older readers do not scan enhanced image records as corrupt legacy ciphertext");
        foreach (var metadata in Directory.GetFiles(legacyDirectory, "*.meta")) File.Delete(metadata);
        var reopened = new HistoryStore(legacyDirectory, protector); await reopened.InitializeAsync();
        var once = await reopened.ListAsync(); var twice = await reopened.ListAsync();
        Check(once.Count == 1 && File.Exists(rewrittenPath), "enhanced payload survives old-reader optional metadata cleanup");
        Check(ReferenceEquals(once, twice), "unchanged history shares a read-only ordered metadata snapshot");
        Check(once is System.Collections.Generic.IList<ClipSummary> list && list.IsReadOnly, "cached metadata snapshot cannot be structurally modified by callers");
        await reopened.SetPinnedAsync(clip.Id, false);
        Check(!ReferenceEquals(once, await reopened.ListAsync()) && once[0].Pinned, "metadata mutation invalidates snapshot without changing prior view");
        var fallbackDirectory = Path.Combine(root, "enhanced-fallback"); Directory.CreateDirectory(fallbackDirectory);
        await File.WriteAllBytesAsync(Path.Combine(fallbackDirectory, clip.Id + ".clip"), legacyEncrypted);
        await File.WriteAllBytesAsync(Path.Combine(fallbackDirectory, clip.Id + ".clip2"), [0, 1, 2]);
        var fallback = new HistoryStore(fallbackDirectory, protector); await fallback.InitializeAsync();
        var fallbackRead = await fallback.GetAsync(clip.Id);
        Check(fallback.UnreadableEntries == 1 && fallbackRead!.ImagePng!.SequenceEqual(png), "corrupt enhanced migration falls back to intact legacy payload");
        CryptographicOperations.ZeroMemory(fallbackRead!.ImagePng!);
        var unavailableDirectory = Path.Combine(root, "summary-encryption-unavailable");
        var unavailable = new HistoryStore(unavailableDirectory, new UnavailableSummaryProtector()); await unavailable.InitializeAsync();
        Check(await unavailable.AddAsync(clip), "optional metadata encryption failure does not reject committed payload");
        var recovered = new HistoryStore(unavailableDirectory, new UnavailableSummaryProtector()); await recovered.InitializeAsync();
        var recoveredImage = await recovered.GetAsync(clip.Id);
        Check(recoveredImage!.ImagePng!.SequenceEqual(png) && recovered.UnreadableEntries == 0,
            "optional summary failure during restart cannot delete an authoritative image");
        CryptographicOperations.ZeroMemory(recoveredImage.ImagePng);
        var blockedCacheDirectory = Path.Combine(root, "summary-write-unavailable");
        var blockedCache = new HistoryStore(blockedCacheDirectory, new WindowsProtector()); await blockedCache.InitializeAsync();
        var blockedTemporary = Path.Combine(blockedCacheDirectory, clip.Id + ".meta.tmp");
        using (var blocked = new FileStream(blockedTemporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Check(await blockedCache.AddAsync(clip), "optional summary write/cleanup failure preserves authoritative payload commit");
        var cacheReopened = new HistoryStore(blockedCacheDirectory, new WindowsProtector()); await cacheReopened.InitializeAsync();
        Check((await cacheReopened.ListAsync()).Count == 1 && cacheReopened.UnreadableEntries == 0,
            "restart cleans stale encrypted metadata staging without deleting a good image");
        await CheckStorageEncodingAllocationsAsync(root, clip);
        Console.WriteLine($"Image storage bytes: legacy={legacyEncrypted.Length}, binary={rewritten.Length}");
        CryptographicOperations.ZeroMemory(png);
    }
    private static async Task CheckStorageEncodingAllocationsAsync(string root, Clip fixture)
    {
        const int iterations = 8;
        var protector = new WindowsProtector();
        void LegacyEncode()
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(fixture);
            try { _ = protector.Protect(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
        }
        var store = new HistoryStore(Path.Combine(root, "storage-allocation-profile"), protector); await store.InitializeAsync();
        LegacyEncode(); await store.AddAsync(fixture);
        var before = GC.GetTotalAllocatedBytes(true);
        for (var i = 0; i < iterations; i++) LegacyEncode();
        var legacy = GC.GetTotalAllocatedBytes(true) - before;
        before = GC.GetTotalAllocatedBytes(true);
        for (var i = 0; i < iterations; i++) await store.AddAsync(fixture);
        var binary = GC.GetTotalAllocatedBytes(true) - before;
        Check(binary < legacy, "complete new image writes allocate less than legacy encoding alone in isolated fixture");
        Console.WriteLine($"Eight image encoding/write allocations: legacy encoding={legacy}, complete binary writes={binary}");
    }
}
