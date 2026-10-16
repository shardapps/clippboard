using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Clippboard.App.Services;
using Microsoft.Win32;

namespace Clippboard.App.Views;

public partial class RopeWindow
{
    private sealed class PreviewTransfer : IHistoryTransfer
    {
        public Task<BackupState> BeginTransferAsync(string id, bool importing, string path, byte[] password, FileActions.DestinationRevision? expected, CancellationToken cancellation)
            => Task.FromException<BackupState>(new System.IO.IOException("Synthetic preview only."));
        public Task<BackupState?> TransferStateAsync(string id, CancellationToken cancellation) => Task.FromResult<BackupState?>(null);
        public Task<BackupState> CommitTransferAsync(string id, CancellationToken cancellation) => Task.FromException<BackupState>(new System.IO.IOException("Synthetic preview only."));
        public Task CancelTransferAsync(string id) => Task.CompletedTask;
    }
    internal void PreviewBackup()
    { if (!_preview) throw new InvalidOperationException("Synthetic backups are available only in preview mode."); OpenBackupCore(true, true); }
    private void OpenBackup(bool importing) => OpenBackupCore(importing, false);
    private void OpenBackupCore(bool importing, bool synthetic)
    {
        if (Retired || _closing) return;
        var transfer = _preview ? new PreviewTransfer() : _store as IHistoryTransfer;
        if (transfer is null) { SetStatus("Backup transfers require the running listener."); return; }
        DismissSettings(); DismissDetail();
        async Task<BackupSelection?> Choose(CancellationToken cancellation)
        {
            string? selected;
            _externalDialog = true;
            try
            {
                if (importing)
                {
                    var dialog = new OpenFileDialog { Filter = "Clippboard encrypted backup (*.clippboard)|*.clippboard", CheckFileExists = true, Multiselect = false };
                    return dialog.ShowDialog(this) == true ? new(dialog.FileName, null) : null;
                }
                var save = new SaveFileDialog { Filter = "Clippboard encrypted backup (*.clippboard)|*.clippboard", DefaultExt = ".clippboard", AddExtension = true,
                    FileName = $"Clippboard-{DateTime.Now:yyyy-MM-dd}.clippboard", OverwritePrompt = true };
                selected = save.ShowDialog(this) == true ? save.FileName : null;
            }
            finally { _externalDialog = false; }
            return selected is null ? null : new(selected, await FileActions.Revisions.ReadAsync(selected, cancellation));
        }
        var view = new BackupView(importing, transfer, Choose, Animated); view.Dismissed += DismissDetail;
        view.ReviewLayoutChanged += compact => Clippboard.App.Design.SmoothMotion.Move(DetailHost, HeightProperty, Math.Min(compact ? 430 : 640, Math.Max(180, AvailableHeight - 48)), Animated, 260);
        DetailHost.Width = Math.Min(820, Math.Max(200, ActualWidth - 48)); DetailHost.Height = Math.Min(640, Math.Max(180, AvailableHeight - 48));
        DetailHost.Child = view; DetailHost.Visibility = Visibility.Visible; Reveal(DetailHost, 12);
        if (synthetic) view.RenderPreview(new(Guid.NewGuid().ToString("N"), BackupPhase.Ready, Message: "Backup verified. Review these counts before restoring.", Preview: new(18, 4, 2, 17825792)));
    }
}
