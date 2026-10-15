using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Clippboard.Core;
using Clippboard.App.Platform;

internal static partial class Program
{
    private static async Task CheckArchiveAsync(string root)
    {
        var password = Encoding.UTF8.GetBytes("A synthetic backup password 🧵");
        var protector = new WindowsProtector(); var source = new HistoryStore(Path.Combine(root, "archive-source"), protector); await source.InitializeAsync();
        var ordinary = new Clip { Kind = ClipKind.Text, Text = "Private synthetic backup text اردو 🧵", Html = HtmlFixture("<b>اردو 🧵</b>"), Pinned = true };
        var old = new Clip { Kind = ClipKind.Text, Text = "Old synthetic entry", CopiedAt = DateTimeOffset.UtcNow.AddDays(-10) };
        var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aXioAAAAASUVORK5CYII=");
        var image = new Clip { Kind = ClipKind.Image, ImagePng = imageBytes };
        var files = new Clip { Kind = ClipKind.Files, FilePaths = [@"C:\synthetic\missing-文件.txt"] };
        foreach (var item in new[] { ordinary, old, image, files }) Check(await source.AddAsync(item), "backup source fixture stored");
        await source.SaveSnippetAsync(null, "Reusable synthetic reply", "Reply", ["work"]);
        using var output = new MemoryStream(); await PortableArchive.ExportAsync(source, output, password);
        var archive = output.ToArray();
        Check(archive.AsSpan(0, 8).SequenceEqual("CLIPBAK1"u8) && archive.AsSpan().IndexOf(Encoding.UTF8.GetBytes(ordinary.Text!)) < 0,
            "portable archive exposes version header but not clipboard plaintext");
        var reads = 0; byte[]? returnedImage = null;
        await PortableArchive.ReadAsync(new MemoryStream(archive), password, clip =>
        { reads++; if (clip.Kind == ClipKind.Image) { returnedImage = clip.ImagePng; Check(returnedImage!.SequenceEqual(imageBytes), "archive keeps raw original PNG without Base64 conversion"); } return Task.CompletedTask; });
        Check(reads == 5 && returnedImage!.All(value => value == 0), "archive reads one record at a time and wipes callback-owned image after use");
        var destinationPath = Path.Combine(root, "archive-destination"); var destination = new HistoryStore(destinationPath, protector); await destination.InitializeAsync();
        using (var staged = await destination.StageRestoreAsync(new MemoryStream(archive), password))
        {
            Check(staged.Preview.NewNotes == 5 && (await destination.ListAsync()).Count == 0, "authenticated restore preview stages encrypted notes without changing history");
            var restored = await destination.CommitRestoreAsync(staged);
            Check(restored.NewNotes == 5 && (await destination.GetAsync(ordinary.Id)) is { Pinned: true, HasRichText: true } restoredText
                && restoredText.CopiedAt == ordinary.CopiedAt && restoredText.Html == ordinary.Html, "explicit restore preserves source age, star, identity and formatting");
            Check((await destination.GetAsync(files.Id))!.FilePaths!.SequenceEqual(files.FilePaths!), "portable file records remain original path references without file copying");
        }
        var restart = new HistoryStore(destinationPath, protector); await restart.InitializeAsync();
        Check((await restart.ListAsync()).Count == 5 && Directory.GetDirectories(destinationPath, "restore-*").Length == 0, "committed restore survives restart and removes encrypted staging");
        using (var duplicate = await restart.StageRestoreAsync(new MemoryStream(archive), password))
        { Check(duplicate.Preview.AlreadyPresent == 5 && duplicate.Preview.NewNotes == 0 && (await restart.CommitRestoreAsync(duplicate)).NewNotes == 0, "reimport skips matching content without overwriting existing notes"); }
        var conflictPath = Path.Combine(root, "archive-conflict"); var conflict = new HistoryStore(conflictPath, protector); await conflict.InitializeAsync();
        await conflict.AddAsync(ordinary with { Text = "Existing conflicting words", Html = null });
        using (var collision = await conflict.StageRestoreAsync(new MemoryStream(archive), password))
        {
            await conflict.CommitRestoreAsync(collision);
            Check((await conflict.GetAsync(ordinary.Id))!.Text == "Existing conflicting words"
                && (await conflict.ListAsync()).Any(item => item.Id != ordinary.Id && item.Fingerprint == ordinary.Fingerprint()), "identity collision imports a separate copy and preserves the existing original");
        }
        var shortRetention = new HistoryStore(Path.Combine(root, "archive-retention"), protector, new(RetentionDays: 7)); await shortRetention.InitializeAsync();
        using (var preview = await shortRetention.StageRestoreAsync(new MemoryStream(archive), password))
            Check(preview.Preview.Expired == 1 && preview.Preview.NewNotes == 4, "restore applies destination retention without renewing archived timestamps");
        var tight = new HistoryStore(Path.Combine(root, "archive-tight"), protector, new(2)); await tight.InitializeAsync(); var existing = new Clip { Kind = ClipKind.Text, Text = "Keep this existing note" }; await tight.AddAsync(existing);
        var failed = false;
        try { using var _ = await tight.StageRestoreAsync(new MemoryStream(archive), password); } catch (InvalidOperationException) { failed = true; }
        Check(failed && (await tight.ListAsync()).Count == 1 && await tight.GetAsync(existing.Id) is not null
            && Directory.GetDirectories(Path.Combine(root, "archive-tight"), "restore-*").Length == 0, "over-budget inspection never evicts history and cleans partial staging");
        var freshPath = Path.Combine(root, "archive-cancel"); var fresh = new HistoryStore(freshPath, protector); await fresh.InitializeAsync();
        using (var preview = await fresh.StageRestoreAsync(new MemoryStream(archive), password))
        {
            await fresh.ClearAsync(); failed = false;
            try { await fresh.CommitRestoreAsync(preview); } catch (InvalidOperationException) { failed = true; }
            Check(failed && (await fresh.ListAsync()).Count == 0, "clear invalidates older restore previews so saved contents cannot reappear");
        }
        using (var preview = await fresh.StageRestoreAsync(new MemoryStream(archive), password))
        {
            await fresh.UpdateLimitsAsync(new(1)); failed = false;
            try { await fresh.CommitRestoreAsync(preview); } catch (InvalidOperationException) { failed = true; }
            Check(failed && (await fresh.ListAsync()).Count == 0, "commit rechecks changed capacity without partially importing");
        }
        await fresh.UpdateLimitsAsync(new());
        using (var preview = await fresh.StageRestoreAsync(new MemoryStream(archive), password))
        {
            var stagedFile = Directory.GetFiles(Directory.GetDirectories(freshPath, "restore-*").Single()).First();
            var changed = await File.ReadAllBytesAsync(stagedFile); changed[^1] ^= 1; await File.WriteAllBytesAsync(stagedFile, changed); failed = false;
            try { await fresh.CommitRestoreAsync(preview); } catch (InvalidDataException) { failed = true; }
            Check(failed && (await fresh.ListAsync()).Count == 0, "same-length staging corruption is detected before the restore commit point");
        }
        using (var cancel = new CancellationTokenSource())
        {
            failed = false;
            try { using var _ = await fresh.StageRestoreAsync(new MemoryStream(archive), password, cancel.Token, (done, _) => { if (done == 1) cancel.Cancel(); }); }
            catch (OperationCanceledException) { failed = true; }
            Check(failed && (await fresh.ListAsync()).Count == 0 && Directory.GetDirectories(freshPath, "restore-*").Length == 0, "canceled import discards encrypted staging and leaves history unchanged");
        }
        foreach (var corrupt in new[] { archive[..^1], archive.Concat(new byte[] { 1 }).ToArray(), Mutated(archive, archive.Length - 1), Mutated(archive, 45), Mutated(archive, 8) })
        {
            failed = false;
            try { using var _ = await fresh.StageRestoreAsync(new MemoryStream(corrupt), password); }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException or EndOfStreamException) { failed = true; }
            Check(failed && (await fresh.ListAsync()).Count == 0 && Directory.GetDirectories(freshPath, "restore-*").Length == 0,
                "truncated, appended, tampered or unsupported archive cannot commit any notes");
        }
        failed = false;
        try { using var _ = await fresh.StageRestoreAsync(new MemoryStream(archive), Encoding.UTF8.GetBytes("Wrong synthetic password")); } catch (CryptographicException) { failed = true; }
        Check(failed && (await fresh.ListAsync()).Count == 0, "wrong backup password leaves history unchanged");
        await CheckRestoreRecoveryAsync(root, archive, password);
        await CheckArchiveBoundsAsync(password);
        var brokenPath = Path.Combine(root, "archive-startup-bounds"); var broken = new HistoryStore(brokenPath, protector); await broken.InitializeAsync();
        await broken.AddAsync(image); var header = new byte[16]; "CLIPIMG2"u8.CopyTo(header); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), int.MaxValue);
        await File.WriteAllBytesAsync(Path.Combine(brokenPath, image.Id + ".clip2"), header);
        var repaired = new HistoryStore(brokenPath, protector); await repaired.InitializeAsync();
        Check((await repaired.ListAsync()).Count == 0 && repaired.UnreadableEntries == 1, "invalid binary payload bounds are handled during startup without an uncaught format exception");
        CryptographicOperations.ZeroMemory(password);
    }
    private static byte[] Mutated(byte[] data, int offset) { var clone = data.ToArray(); clone[offset] ^= 0x40; return clone; }
    private static async Task CheckRestoreRecoveryAsync(string root, byte[] archive, byte[] password)
    {
        var path = Path.Combine(root, "archive-recovery"); var protector = new WindowsProtector(); var history = new HistoryStore(path, protector); await history.InitializeAsync();
        var original = new Clip { Kind = ClipKind.Text, Text = "Original survives interrupted restore" }; await history.AddAsync(original);
        using var preview = await history.StageRestoreAsync(new MemoryStream(archive), password);
        var folder = Directory.GetDirectories(path, "restore-*").Single();
        var files = Directory.GetFiles(folder).Select(file => new { Id = Path.GetFileNameWithoutExtension(file), Name = Path.GetFileName(file), Length = new FileInfo(file).Length,
            Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) }).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(path, "restore.pending"), protector.Protect(JsonSerializer.SerializeToUtf8Bytes(new { Folder = Path.GetFileName(folder), Files = files })));
        File.Move(Path.Combine(folder, files[0].Name), Path.Combine(path, files[0].Name));
        var restart = new HistoryStore(path, protector); await restart.InitializeAsync();
        Check((await restart.ListAsync()).Single().Id == original.Id && !File.Exists(Path.Combine(path, "restore.pending")) && !Directory.Exists(folder),
            "interrupted restore journal rolls back only newly introduced records and removes staging");
        var malformedFolder = "restore-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(Path.Combine(path, malformedFolder));
        await File.WriteAllBytesAsync(Path.Combine(path, "restore.pending"), protector.Protect(JsonSerializer.SerializeToUtf8Bytes(new { Folder = malformedFolder,
            Files = new[] { new { Id = original.Id, Name = "../outside.clip", Length = 5, Hash = new string('0', 64) } } })));
        var rejected = false;
        try { await new HistoryStore(path, protector).InitializeAsync(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && File.Exists(Path.Combine(path, original.Id + ".clip")), "unsafe recovery paths fail closed without deleting original history");
        File.Delete(Path.Combine(path, "restore.pending"));
        await restart.InitializeAsync();
        Check(!Directory.Exists(Path.Combine(path, malformedFolder)), "abandoned managed staging is cleaned on startup");
    }
    private static async Task CheckArchiveBoundsAsync(byte[] password)
    {
        // Independent encoder fixture: fixed salt/prefix, explicit nonce/AAD
        // layout, and AES-GCM from the platform instead of the product writer.
        static byte[] PlainRecord(Clip clip, int? count = null, int? size = null)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(clip); using var plain = new MemoryStream();
            var word = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(word, count ?? 1); plain.Write(word);
            BinaryPrimitives.WriteInt32LittleEndian(word, size ?? json.Length); plain.Write(word); plain.Write(json);
            BinaryPrimitives.WriteInt32LittleEndian(word, 0); plain.Write(word); return plain.ToArray();
        }
        byte[] Seal(byte[] plaintext)
        {
            var header = new byte[36]; "CLIPBAK1"u8.CopyTo(header); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 600000);
            for (var i = 12; i < 36; i++) header[i] = (byte)i;
            var key = Rfc2898DeriveBytes.Pbkdf2(password, header.AsSpan(12, 16), 600000, HashAlgorithmName.SHA256, 32);
            using var cipher = new AesGcm(key, 16); using var output = new MemoryStream(); output.Write(header);
            uint index = 0;
            for (var position = 0; ;)
            {
                var count = Math.Min(65536, plaintext.Length - position); var nonce = new byte[12]; header.AsSpan(28, 8).CopyTo(nonce); BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), index);
                var aad = new byte[44]; header.CopyTo(aad, 0); BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(36), index++); BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(40), count);
                var encrypted = new byte[count]; var tag = new byte[16]; cipher.Encrypt(nonce, plaintext.AsSpan(position, count), encrypted, tag, aad);
                output.Write(aad, 40, 4); output.Write(encrypted); output.Write(tag); position += count;
                if (count == 0) break;
            }
            CryptographicOperations.ZeroMemory(key); return output.ToArray();
        }
        var large = new Clip { Kind = ClipKind.Text, Text = new string('x', 150000) };
        var valid = Seal(PlainRecord(large)); var records = 0;
        await PortableArchive.ReadAsync(new MemoryStream(valid), password, clip => { records++; Check(clip.Text == large.Text, "independent format fixture decodes exact multi-chunk text"); return Task.CompletedTask; });
        Check(records == 1, "independent AES-GCM fixture validates published nonce and associated-data layout");
        const int frame = 4 + 65536 + 16; var reordered = valid.ToArray();
        valid.AsSpan(36 + frame, frame).CopyTo(reordered.AsSpan(36)); valid.AsSpan(36, frame).CopyTo(reordered.AsSpan(36 + frame));
        var failed = false;
        try { await PortableArchive.ReadAsync(new MemoryStream(reordered), password, _ => Task.CompletedTask); } catch (CryptographicException) { failed = true; }
        Check(failed, "authenticated chunk position rejects reordered equal-sized blocks");
        foreach (var plain in new[] { PlainRecord(large, count: 1001), PlainRecord(large, size: int.MaxValue), PlainRecord(large with { Id = "../unsafe" }),
            PlainRecord(large with { CopiedAt = DateTimeOffset.UtcNow.AddDays(1) }), PlainRecord(large with { Kind = ClipKind.Files, Text = null, FilePaths = ["relative.txt"] }) })
        {
            failed = false;
            try { await PortableArchive.ReadAsync(new MemoryStream(Seal(plain)), password, _ => Task.CompletedTask); }
            catch (InvalidDataException) { failed = true; }
            Check(failed, "authenticated but malformed record obeys count, allocation, identity, age and file-reference bounds");
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
