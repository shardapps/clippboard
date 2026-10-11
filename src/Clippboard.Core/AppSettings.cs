using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Clippboard.Core;

// Append themes to preserve existing serialized numeric values.
public enum AppTheme { Cloud, Paper, Midnight, Lavender, Ocean, Rose }
public enum ShortcutChoice { CtrlShiftV, AltV, CtrlAltV }

public sealed record AppSettings
{
    public AppTheme Theme { get; init; } = AppTheme.Cloud;
    public int RetentionDays { get; init; } = 30;
    public int MaxEntries { get; init; } = 500;
    public int StorageMiB { get; init; } = 256;
    public int PreviewCacheMiB { get; init; } = 16;
    public int NoteWidth { get; init; } = 300;
    public int NoteSpacing { get; init; } = 40;
    public int TextSize { get; init; } = 17;
    public bool Animate { get; init; } = true;
    public bool PlayfulMotion { get; init; } = true;
    public bool LowMemoryRendering { get; init; }
    public bool AutoPaste { get; init; } = true;
    public bool StarredFirst { get; init; } = true;
    public bool OpenStarredOnly { get; init; }
    public bool StartAtLogin { get; init; }
    public bool CaptureText { get; init; } = true;
    public bool CaptureImages { get; init; } = true;
    public bool CaptureFiles { get; init; } = true;
    public bool CapturePaused { get; init; }
    public ShortcutChoice Shortcut { get; init; } = ShortcutChoice.CtrlShiftV;

    public AppSettings Normalize() => this with
    {
        Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.Cloud,
        Shortcut = Enum.IsDefined(Shortcut) ? Shortcut : ShortcutChoice.CtrlShiftV,
        RetentionDays = Math.Clamp(RetentionDays, 1, 30),
        MaxEntries = Math.Clamp(MaxEntries, 50, 1000),
        StorageMiB = Math.Clamp(StorageMiB, 32, 512),
        PreviewCacheMiB = Math.Clamp(PreviewCacheMiB, 4, 64),
        NoteWidth = Math.Clamp(NoteWidth, 250, 400),
        NoteSpacing = Math.Clamp(NoteSpacing, 24, 96),
        TextSize = Math.Clamp(TextSize, 15, 22)
    };

    public HistoryLimits HistoryLimits() => new(MaxEntries, StorageMiB * 1024L * 1024, RetentionDays);
    public bool Captures(ClipKind kind) => !CapturePaused && kind switch
    {
        ClipKind.Text => CaptureText,
        ClipKind.Image => CaptureImages,
        ClipKind.Files => CaptureFiles,
        _ => false
    };
}

public sealed class SettingsStore(string file)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<AppSettings> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(file)) return new();
            if (new FileInfo(file).Length > 64 * 1024) return new();
            try { return (JsonSerializer.Deserialize<AppSettings>(await File.ReadAllBytesAsync(file)) ?? new()).Normalize(); }
            catch (JsonException) { return new(); }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        var temporary = file + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(settings.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); _gate.Release(); }
    }
}
