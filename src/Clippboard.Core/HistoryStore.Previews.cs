using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Clippboard.Core;

public sealed partial class HistoryStore
{
    // Derived data is optional, encrypted and never displaces authoritative history.
    public const int MaxPreviewBytes = 384 * 1024;
    private readonly Dictionary<string, long> _previewSizes = new();
    private string PreviewFor(string id) => Path.ChangeExtension(FileFor(id), ".thumb");
    private long PreviewBudget => Math.Max(0, Math.Min(_limits.MaxBytes / 16,
        _limits.MaxBytes - _items.Values.Sum(x => x.StoredBytes)));

    private void LoadPreviews()
    {
        _previewSizes.Clear();
        foreach (var file in Directory.EnumerateFiles(directory, "*.thumb"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var size = new FileInfo(file).Length;
            if (!_items.TryGetValue(id, out var item) || item.Kind != ClipKind.Image || size > MaxPreviewBytes + 4096)
                File.Delete(file);
            else _previewSizes[id] = size;
        }
        TrimPreviews();
    }

    private void TrimPreviews(long incoming = 0)
    {
        var total = _previewSizes.Values.Sum();
        var budget = PreviewBudget;
        if (total + incoming <= budget) return;
        foreach (var id in _previewSizes.Keys.ToArray())
        {
            if (total + incoming <= budget) break;
            File.Delete(PreviewFor(id)); total -= _previewSizes[id]; _previewSizes.Remove(id);
        }
    }

    public async Task<byte[]?> GetPreviewAsync(string id)
    {
        Clip? original = null; ClipSummary? summary;
        await _gate.WaitAsync();
        try
        {
            Trim(DateTimeOffset.UtcNow);
            if (!_items.TryGetValue(id, out summary) || summary.Kind != ClipKind.Image) return null;
            if (createPreview is not null && _previewSizes.ContainsKey(id))
            {
                byte[]? plaintext = null;
                try
                {
                    var file = PreviewFor(id);
                    if (new FileInfo(file).Length > MaxPreviewBytes + 4096) throw new InvalidDataException("Invalid preview size.");
                    plaintext = protector.Unprotect(await File.ReadAllBytesAsync(file));
                    var fingerprint = Convert.FromHexString(summary.Fingerprint.Split(':')[1]);
                    if (plaintext.Length is <= 33 or > MaxPreviewBytes + 33 || plaintext[0] != 1
                        || !CryptographicOperations.FixedTimeEquals(plaintext.AsSpan(1, 32), fingerprint))
                        throw new InvalidDataException("Stale preview.");
                    return plaintext[33..];
                }
                catch (Exception ex) when (ex is IOException or CryptographicException or ArgumentException or UnauthorizedAccessException)
                {
                    // A corrupt cache is rebuilt from the untouched original.
                    _previewSizes.Remove(id);
                    try { File.Delete(PreviewFor(id)); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
                }
                finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
            }
            original = await ReadFileAsync(FileFor(id));
        }
        finally { _gate.Release(); }

        if (createPreview is null) return original.ImagePng;
        byte[]? preview = null;
        try
        {
            if (original.ImagePng is null) return null;
            preview = createPreview(original.ImagePng);
            if (preview.Length is <= 0 or > MaxPreviewBytes || ReferenceEquals(preview, original.ImagePng))
                throw new InvalidDataException("Invalid derived preview.");
            await _gate.WaitAsync();
            try
            {
                // Deletion/expiry can happen while the decoder is working.
                Trim(DateTimeOffset.UtcNow);
                if (!_items.TryGetValue(id, out var current) || current.Fingerprint != summary.Fingerprint) return null;
                var plaintext = new byte[preview.Length + 33];
                plaintext[0] = 1;
                Convert.FromHexString(summary.Fingerprint.Split(':')[1]).CopyTo(plaintext, 1);
                preview.CopyTo(plaintext, 33);
                byte[] encrypted;
                try { encrypted = protector.Protect(plaintext); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
                var file = PreviewFor(id); var temporary = file + ".tmp";
                try
                {
                    // Remove an earlier concurrent result before accounting for replacement.
                    _previewSizes.Remove(id);
                    TrimPreviews(encrypted.Length);
                    if (encrypted.Length <= PreviewBudget - _previewSizes.Values.Sum())
                    {
                        await File.WriteAllBytesAsync(temporary, encrypted);
                        File.Move(temporary, file, true); _previewSizes[id] = encrypted.Length;
                    }
                    else File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Cache failures do not prevent viewing. */ }
                finally { try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            }
            finally { _gate.Release(); }
            var result = preview; preview = null; return result;
        }
        finally
        {
            if (original.ImagePng is not null) CryptographicOperations.ZeroMemory(original.ImagePng);
            if (preview is not null) CryptographicOperations.ZeroMemory(preview);
        }
    }
}
