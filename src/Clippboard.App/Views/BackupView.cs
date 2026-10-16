using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using Clippboard.App.Design;
using Clippboard.App.Services;

namespace Clippboard.App.Views;

internal sealed record BackupSelection(string Path, FileActions.DestinationRevision? Revision);
internal sealed class BackupView : UserControl
{
    private readonly bool _importing, _animate;
    private readonly IHistoryTransfer _transfer;
    private readonly Func<CancellationToken, Task<BackupSelection?>> _choose;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _cancellation;
    private readonly TextBox _path = new() { IsReadOnly = true, IsUndoEnabled = false, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    private readonly PasswordBox _password = PasswordField("Backup password"), _confirm = PasswordField("Confirm backup password");
    private readonly StackPanel _inputs = new();
    private readonly Button _start = new(), _restore = new() { Content = "Restore notes", Visibility = Visibility.Collapsed }, _cancel = new() { Content = "Cancel transfer", Visibility = Visibility.Collapsed };
    private readonly TextBlock _notice = new() { FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 14) };
    private readonly Border _track = new() { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 12, 0, 0) };
    private readonly Border _fill = new() { Width = 0, Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
    private BackupSelection? _selection;
    private string? _jobId;
    private bool _working, _polling, _unloaded, _choosing;
    private double _fraction;
    internal event Action? Dismissed;
    internal event Action<bool>? ReviewLayoutChanged;
    internal BackupState? State { get; private set; }
    internal void RenderPreview(BackupState state) => Apply(state);
    internal BackupView(bool importing, IHistoryTransfer transfer, Func<CancellationToken, Task<BackupSelection?>> choose, bool animate)
    {
        _importing = importing; _transfer = transfer; _choose = choose; _animate = animate; _cancellation = _lifetime.Token;
        var body = new StackPanel { Margin = new Thickness(24) };
        var header = new DockPanel(); var close = RopeWindow.IconButton("close", "Close backup transfer");
        close.Click += (_, _) => Dismissed?.Invoke(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = importing ? "Restore a private backup" : "Take a private backup", FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }); body.Children.Add(header);
        body.Children.Add(new TextBlock { Text = importing ? "Inspect the encrypted backup first. Nothing changes until you choose Restore."
            : "Password-encrypted and portable to another PC. Choose a strong passphrase; there is no password reset.", TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 14, 0, 8) });
        var chooseButton = new Button { Content = importing ? "Choose backup…" : "Choose destination…", HorizontalAlignment = HorizontalAlignment.Left };
        chooseButton.Click += async (_, _) => await ChooseAsync(); _inputs.Children.Add(chooseButton); _inputs.Children.Add(_path);
        _inputs.Children.Add(new TextBlock { Text = "Passphrase (12–256 characters)", Margin = new Thickness(0, 8, 0, 6) }); _inputs.Children.Add(_password);
        if (!importing) { _inputs.Children.Add(new TextBlock { Text = "Confirm passphrase", Margin = new Thickness(0, 10, 0, 6) }); _inputs.Children.Add(_confirm); }
        body.Children.Add(_inputs);
        _start.Content = importing ? "Inspect backup" : "Save encrypted backup"; _start.SetResourceReference(BackgroundProperty, "SelectedSurface");
        _restore.SetResourceReference(BackgroundProperty, "SelectedSurface"); _restore.FontWeight = FontWeights.SemiBold;
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) }; actions.Children.Add(_start); actions.Children.Add(_restore); actions.Children.Add(_cancel); body.Children.Add(actions);
        _track.SetResourceReference(BackgroundProperty, "Outline"); _fill.SetResourceReference(BackgroundProperty, "Accent"); _track.Child = _fill; body.Children.Add(_track); body.Children.Add(_notice);
        _track.SizeChanged += (_, _) => SmoothMotion.Move(_fill, WidthProperty, Math.Max(0, _track.ActualWidth * _fraction), false, 0);
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_notice, System.Windows.Automation.AutomationLiveSetting.Polite);
        System.Windows.Automation.AutomationProperties.SetName(_path, "Chosen backup path");
        var hint = new TextBlock { Text = "Capture pauses during transfer and import review. Files contain locations, not file contents. Existing notes are preserved; matching notes and expired history are skipped. Original ages and your retention/storage limits still apply. Exported backup files remain until you delete them.", TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(hint);
        _start.Click += async (_, _) => await BeginAsync(); _restore.Click += async (_, _) => await CommitAsync();
        _cancel.Click += async (_, _) => { if (_jobId is null) return; try { await _transfer.CancelTransferAsync(_jobId); await PollAsync(); } catch (Exception ex) when (Recoverable(ex)) { _notice.Text = "Could not reach the listener. Close the rope to cancel its transfer."; } };
        _poll.Tick += async (_, _) => await PollAsync();
        Unloaded += (_, _) => Detach();
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        // A view removed before WPF delivers Loaded might never receive
        // Unloaded. Cancel its preassigned transfer identity on detachment too.
        if (oldParent is not null && VisualTreeHelper.GetParent(this) is null) Detach();
    }
    private void Detach()
    {
        if (_unloaded) return;
        _unloaded = true; _poll.Stop(); _lifetime.Cancel(); _password.Clear(); _confirm.Clear();
        if (_jobId is not null) _ = CancelDetachedAsync(_jobId);
        _lifetime.Dispose();
    }
    private static PasswordBox PasswordField(string name)
    {
        var box = new PasswordBox { MaxLength = 256, FontSize = 17, Padding = new Thickness(12, 9, 12, 9), BorderThickness = new Thickness(1) };
        box.SetResourceReference(BackgroundProperty, "Chrome"); box.SetResourceReference(ForegroundProperty, "Foreground"); box.SetResourceReference(BorderBrushProperty, "Outline");
        System.Windows.Automation.AutomationProperties.SetName(box, name); return box;
    }
    internal async Task ChooseAsync()
    {
        if (_choosing || _working || _unloaded) return; _choosing = true;
        try { var selection = await _choose(_cancellation); if (!_unloaded && !_cancellation.IsCancellationRequested && selection is not null) { _selection = selection; _path.Text = selection.Path; } }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (Recoverable(ex)) { _notice.Text = "Could not inspect this location. Choose another file or folder."; }
        finally { _choosing = false; }
    }
    internal async Task BeginAsync()
    {
        if (_working || _choosing || _unloaded) return;
        if (_selection is null) { _notice.Text = "Choose a backup file or destination first."; return; }
        _working = true; _start.IsEnabled = false; var started = false; byte[]? secret = null, confirmation = null;
        try
        {
            using var secured = _password.SecurePassword; secret = PasswordBuffer.Encode(secured);
            if (!_importing)
            {
                using var confirmed = _confirm.SecurePassword; confirmation = PasswordBuffer.Encode(confirmed);
                if (!CryptographicOperations.FixedTimeEquals(secret, confirmation)) { _notice.Text = "The passphrases do not match."; return; }
            }
            _jobId = Guid.NewGuid().ToString("N");
            var state = await _transfer.BeginTransferAsync(_jobId, _importing, _selection.Path, secret, _selection.Revision, _cancellation);
            _password.Clear(); _confirm.Clear();
            if (_unloaded || _cancellation.IsCancellationRequested || !IsVisible || PresentationSource.FromVisual(this) is null) return;
            Apply(state); started = true;
            if (state.Phase is not (BackupPhase.Completed or BackupPhase.Canceled or BackupPhase.Failed)) _poll.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (Recoverable(ex)) { if (!_unloaded) _notice.Text = ex is ArgumentException ? "Use matching passphrases of 12–256 complete characters." : "Could not start this transfer. Finish any current transfer and check the selected location."; }
        finally
        {
            if (secret is not null) CryptographicOperations.ZeroMemory(secret); if (confirmation is not null) CryptographicOperations.ZeroMemory(confirmation);
            _working = false; if (!_unloaded && !started) _start.IsEnabled = true;
        }
    }
    internal async Task CommitAsync()
    {
        if (_jobId is null || _working || _unloaded) return;
        _working = true; _restore.IsEnabled = false;
        try { var state = await _transfer.CommitTransferAsync(_jobId, _cancellation); if (!_unloaded) Apply(state); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (Recoverable(ex)) { if (!_unloaded) { _notice.Text = "Could not start the restore. Inspect the backup again if its preview expired."; _restore.IsEnabled = true; } }
        finally { _working = false; }
    }
    internal async Task PollAsync()
    {
        if (_polling || _unloaded || _jobId is null) return; _polling = true;
        try
        {
            var state = await _transfer.TransferStateAsync(_jobId, _cancellation);
            if (_unloaded || _cancellation.IsCancellationRequested || !IsVisible || PresentationSource.FromVisual(this) is null) return;
            if (state is not null) Apply(state); else { _poll.Stop(); _notice.Text = "This transfer is no longer available. Inspect or start it again."; _inputs.IsEnabled = _start.IsEnabled = true; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (Recoverable(ex)) { if (!_unloaded) { _poll.Stop(); _notice.Text = "The listener is unavailable. Close the rope, then reopen it to retry."; } }
        finally { _polling = false; }
    }
    private void Apply(BackupState state)
    {
        if (State == state) return;
        var wasReviewing = State?.Phase is BackupPhase.Ready or BackupPhase.Restoring; State = state;
        var reviewing = state.Phase is BackupPhase.Ready or BackupPhase.Restoring;
        _inputs.Visibility = reviewing ? Visibility.Collapsed : Visibility.Visible;
        if (wasReviewing != reviewing) ReviewLayoutChanged?.Invoke(reviewing);
        var terminal = state.Phase is BackupPhase.Completed or BackupPhase.Canceled or BackupPhase.Failed;
        _inputs.IsEnabled = _start.IsEnabled = terminal; _start.Visibility = terminal ? Visibility.Visible : Visibility.Collapsed;
        _restore.Visibility = state.Phase == BackupPhase.Ready ? Visibility.Visible : Visibility.Collapsed; _restore.IsEnabled = true;
        _cancel.Visibility = terminal ? Visibility.Collapsed : Visibility.Visible;
        _fraction = state.Total > 0 ? Math.Clamp(state.Done / (double)state.Total, 0, 1) : state.Phase is BackupPhase.Completed or BackupPhase.Ready ? 1 : 0;
        SmoothMotion.Move(_fill, WidthProperty, Math.Max(0, _track.ActualWidth * _fraction), _animate && Interaction.GetEnabled(this), 180);
        _notice.Text = state.Preview is { } preview ? $"{state.Message}\n{preview.NewNotes} new · {preview.AlreadyPresent} already present · {preview.Expired} expired · {preview.EncryptedBytes / 1048576.0:0.#} MiB encrypted"
            : state.Message ?? $"{state.Phase}… {(state.Total > 0 ? $"{state.Done} of {state.Total} notes" : "")}";
        if (state.Phase == BackupPhase.Ready) _restore.Content = $"Restore {state.Preview?.NewNotes ?? 0} notes";
        if (terminal) _poll.Stop();
    }
    private async Task CancelDetachedAsync(string id) { try { await _transfer.CancelTransferAsync(id); } catch (Exception ex) when (Recoverable(ex) || ex is OperationCanceledException or ObjectDisposedException) { } }
    private static bool Recoverable(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or CryptographicException;
}
