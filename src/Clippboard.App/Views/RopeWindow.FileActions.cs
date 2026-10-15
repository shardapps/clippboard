using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.Win32;
using Clippboard.App.Services;
using Clippboard.Core;

namespace Clippboard.App.Views;

public partial class RopeWindow
{
    private bool _externalDialog, _exporting;
    private readonly System.Threading.CancellationTokenSource _fileActionsLifetime = new();
    private readonly FileAvailabilityProbe _locationProbe = new();
    private readonly ShellLocation _shellLocation = new();
    private Button FileActionButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            button.MinWidth = Math.Max(button.MinWidth, button.ActualWidth); button.Content = "Working…";
            try { await action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException
                or System.Security.SecurityException or CryptographicException or System.Runtime.InteropServices.ExternalException or InvalidOperationException)
            { if (!Retired && !_closing) SetStatus(ex is FileAvailabilityException or FileActionException ? ex.Message : "Could not complete this action. Check the original location, destination access and free space."); }
            finally { button.Content = label; button.IsEnabled = true; }
        };
        return button;
    }
    private void AddFileActions(Panel footer, string[] paths)
    {
        var copy = FileActionButton("Copy paths", async () => { if (EditedTextSelected is not null) await EditedTextSelected(FileActions.PathsAsText(paths)); });
        footer.Children.Add(copy);
        var locate = FileActionButton(paths.Length > 1 ? "First location" : "Show location", async () =>
        {
            if (paths.Length == 0 || !await _locationProbe.CheckAsync([paths[0]])) throw new FileAvailabilityException("The original was moved, deleted or disconnected. Copy it again to update this note.");
            if (Retired || _closing) return;
            if (!await _shellLocation.SelectAsync(paths[0])) throw new FileActionException("Explorer could not select this original. Copy its path or restore it manually.");
        });
        footer.Children.Add(locate);
    }
    private async Task SaveImageAsync(string id)
    {
        if (_exporting || Retired || _closing) return;
        var cancellation = _fileActionsLifetime.Token;
        _exporting = true;
        Clip? clip = null;
        try
        {
            var dialog = new SaveFileDialog { Title = "Save copied image", Filter = "PNG image (*.png)|*.png", DefaultExt = ".png", AddExtension = true,
                OverwritePrompt = true, FileName = "Clippboard-image-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png" };
            _externalDialog = true; bool? chosen;
            try { chosen = dialog.ShowDialog(this); } finally { _externalDialog = false; }
            if (chosen != true || Retired || _closing) return;
            var destination = await Task.Run(() => FileActions.DestinationState(dialog.FileName), cancellation);
            clip = await Task.Run(() => _store.GetAsync(id));
            cancellation.ThrowIfCancellationRequested();
            if (clip?.Kind != ClipKind.Image || clip.ImagePng is null) throw new IOException("This image expired or was deleted.");
            await Task.Run(() => FileActions.SavePngAsync(dialog.FileName, clip.ImagePng, cancellation, destination), cancellation);
            if (!Retired && !_closing) SetStatus("PNG saved to your chosen location. Original clipboard note preserved.");
        }
        catch (OperationCanceledException) { }
        finally { if (clip?.ImagePng is not null) CryptographicOperations.ZeroMemory(clip.ImagePng); _externalDialog = false; _exporting = false; }
    }
}
