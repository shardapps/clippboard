using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clippboard.App.Services;

internal static partial class Program
{
    private static async Task CheckFileActionsAsync(string root)
    {
        var directory = Path.Combine(root, "explicit-image-export"); Directory.CreateDirectory(directory);
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var image = new MemoryStream(); encoder.Save(image); var png = image.ToArray();
        var target = Path.Combine(directory, "sample.png"); await FileActions.SavePngAsync(target, png);
        await FileActions.SavePngAsync(target, png, expected: FileActions.DestinationState(target));
        Check((await File.ReadAllBytesAsync(target)).SequenceEqual(png), "successful PNG replacement releases its own destination guard before Windows atomic move");
        Check((await File.ReadAllBytesAsync(target)).SequenceEqual(png), "explicit export preserves original PNG bytes");
        Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "successful export removes staging files");
        await File.WriteAllTextAsync(target, "existing destination");
        var failed = false;
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        { try { await FileActions.SavePngAsync(target, png); } catch (IOException) { failed = true; } }
        Check(failed && await File.ReadAllTextAsync(target) == "existing destination"
            && Directory.GetFiles(directory, "*.tmp").Length == 0, "replacement failure preserves destination and removes partial export");
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); failed = false;
        try { await FileActions.SavePngAsync(target, png, canceled.Token); } catch (OperationCanceledException) { failed = true; }
        Check(failed && await File.ReadAllTextAsync(target) == "existing destination", "canceled export cannot truncate an existing file");
        failed = false;
        try { await FileActions.SavePngAsync(Path.Combine(directory, "bad.txt"), png); } catch (FileActionException) { failed = true; }
        Check(failed && !File.Exists(Path.Combine(directory, "bad.txt")), "PNG export rejects mismatched extension before writing");
        failed = false;
        try { await FileActions.SavePngAsync(target, [1, 2, 3]); } catch (FileActionException) { failed = true; }
        Check(failed && await File.ReadAllTextAsync(target) == "existing destination", "invalid image does not touch export destination");
        var revision = FileActions.DestinationState(target); await File.WriteAllTextAsync(target, "changed by another app"); failed = false;
        try { await FileActions.SavePngAsync(target, png, expected: revision); } catch (FileActionException) { failed = true; }
        Check(failed && await File.ReadAllTextAsync(target) == "changed by another app", "stale export destination cannot overwrite another app's changes");
        Check(FileActions.PathsAsText(["C:\\folder with spaces\\note.txt", "C:\\اردو\\data.csv"])
            == "C:\\folder with spaces\\note.txt" + Environment.NewLine + "C:\\اردو\\data.csv", "path text preserves Unicode and separates original references");
        failed = false;
        try { FileActions.PathsAsText(Enumerable.Repeat(new string('x', 32000), 100).ToArray()); } catch (FileActionException) { failed = true; }
        Check(failed, "path copy rejects oversized combined text before allocation");
        var apartment = ApartmentState.Unknown;
        var location = new ShellLocation(path => { apartment = Thread.CurrentThread.GetApartmentState(); return path == "C:\\folder with spaces\\note, اردو.txt"; });
        Check(await location.SelectAsync("C:\\folder with spaces\\note, اردو.txt") && apartment == ApartmentState.STA,
            "location selection preserves spaces, commas and Unicode on its owned STA without command parsing");
        failed = false;
        try { FileActions.ValidateLocation("relative.txt"); } catch (FileActionException) { failed = true; }
        Check(failed, "location action rejects an ambiguous relative original");
        using var release = new ManualResetEventSlim(); var calls = 0;
        var stalled = new ShellLocation(_ => { Interlocked.Increment(ref calls); release.Wait(); return true; }, TimeSpan.FromMilliseconds(60));
        try
        {
            failed = false; try { await stalled.SelectAsync(target); } catch (FileAvailabilityException) { failed = true; }
            Check(failed && stalled.HasOutstandingOperation, "slow shell returns a bounded deadline without blocking UI");
            for (var i = 0; i < 10; i++) try { await stalled.SelectAsync(target); } catch (FileAvailabilityException) { }
            Check(calls == 1, "retrying stalled shell selection cannot create more workers");
        }
        finally { release.Set(); }
        for (var i = 0; i < 100 && stalled.HasOutstandingOperation; i++) await Task.Delay(10);
        Check(await stalled.SelectAsync(target) && calls == 2, "shell requests recover after the original operation finishes");
    }
}
