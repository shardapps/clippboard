using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Clippboard.Core;

namespace Clippboard.App.Views;

internal sealed class CombinationView : UserControl
{
    private readonly IReadOnlyList<ClipSummary> _entries;
    private readonly Func<IReadOnlyList<string>, string, CancellationToken, Task<string>> _build;
    private readonly CancellationToken _lifetime;
    private readonly TextBox _preview = new() { IsReadOnly = true, IsUndoEnabled = false, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 17 };
    private readonly TextBox _order = new() { IsReadOnly = true, IsUndoEnabled = false, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 12 };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _copy = new() { Content = "Copy combined text", IsEnabled = false }, _edit = new() { Content = "Edit / save as snippet", IsEnabled = false };
    private readonly SemaphoreSlim _work = new(1, 1);
    private CancellationTokenSource? _pending;
    private int _sort;
    private string _separator = "\n\n";
    private readonly Dictionary<int, Button> _sortButtons = new();
    private readonly Dictionary<string, Button> _separatorButtons = new();
    internal string PreviewText => _preview.Text;
    public event Action? Dismissed, ClearRequested;
    public event Func<string, Task>? CopyRequested, EditRequested;
    internal CombinationView(IReadOnlyList<ClipSummary> entries, Func<IReadOnlyList<string>, string, CancellationToken, Task<string>> build, CancellationToken lifetime)
    {
        _entries = entries; _build = build; _lifetime = lifetime;
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = RopeWindow.IconButton("close", "Close combination preview"); close.Click += (_, _) => Dismissed?.Invoke(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = $"Combine {entries.Count} text notes", FontSize = 23, FontWeight = FontWeights.SemiBold }); root.Children.Add(header);
        var options = new StackPanel(); var ordering = new WrapPanel();
        foreach (var (label, value) in new[] { ("Selection order", 0), ("Newest first", 1), ("Oldest first", 2) })
        { var button = new Button { Content = label }; _sortButtons[value] = button; button.Click += async (_, _) => { _sort = value; await RebuildAsync(); }; ordering.Children.Add(button); }
        options.Children.Add(ordering); options.Children.Add(_order);
        var separators = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        foreach (var (label, value) in new[] { ("Blank line", "\n\n"), ("Line break", "\n"), ("Comma", ", "), ("Space", " "), ("Tab", "\t") })
        { var button = new Button { Content = label }; _separatorButtons[value] = button; button.Click += async (_, _) => { _separator = value; await RebuildAsync(); }; separators.Children.Add(button); }
        var custom = new TextBox { MaxLength = 32, Width = 120, Margin = new Thickness(5, 0, 0, 0), ToolTip = "Custom literal separator (up to 32 characters)" };
        separators.Children.Add(new TextBlock { Text = "Custom:", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) });
        System.Windows.Automation.AutomationProperties.SetName(custom, "Custom combination separator");
        custom.TextChanged += async (_, _) => { _separator = custom.Text; await RebuildAsync(); }; separators.Children.Add(custom); options.Children.Add(separators);
        Grid.SetRow(options, 1); root.Children.Add(options);
        System.Windows.Automation.AutomationProperties.SetName(_preview, "Combined plain-text preview");
        System.Windows.Automation.AutomationProperties.SetName(_order, "Selected note order"); Grid.SetRow(_preview, 2); root.Children.Add(_preview);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; var actions = new WrapPanel();
        _copy.Click += async (_, _) => { if (CopyRequested is not null) await CopyRequested(_preview.Text); }; actions.Children.Add(_copy);
        _edit.Click += async (_, _) => { if (EditRequested is not null) await EditRequested(_preview.Text); }; actions.Children.Add(_edit);
        var clear = new Button { Content = "Clear selection" }; clear.Click += (_, _) => ClearRequested?.Invoke(); actions.Children.Add(clear);
        footer.Children.Add(actions); footer.Children.Add(_notice); Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        Unloaded += (_, _) => { _pending?.Cancel(); _pending?.Dispose(); _pending = null; };
    }
    internal async Task RebuildAsync()
    {
        _pending?.Cancel(); _pending?.Dispose(); using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); _pending = source;
        var cancellation = source.Token;
        IEnumerable<ClipSummary> sequence = _sort switch { 1 => _entries.OrderByDescending(x => x.CopiedAt), 2 => _entries.OrderBy(x => x.CopiedAt), _ => _entries };
        var ordered = sequence.ToArray();
        var separator = _separator; _copy.IsEnabled = _edit.IsEnabled = false; _notice.Text = "Building preview…";
        foreach (var pair in _sortButtons) pair.Value.SetResourceReference(BackgroundProperty, pair.Key == _sort ? "SelectedSurface" : "Chrome");
        foreach (var pair in _separatorButtons) pair.Value.SetResourceReference(BackgroundProperty, pair.Key == separator ? "SelectedSurface" : "Chrome");
        _order.Text = string.Join('\n', ordered.Select((entry, index) => $"{index + 1}. {(string.IsNullOrWhiteSpace(entry.Title) ? entry.Preview[..Math.Min(70, entry.Preview.Length)] : entry.Title).Replace('\r', ' ').Replace('\n', ' ')}"));
        try
        {
            await _work.WaitAsync(cancellation);
            string text; try { text = await _build(ordered.Select(x => x.Id).ToArray(), separator, cancellation); } finally { _work.Release(); }
            cancellation.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_pending, source)) return;
            _preview.Text = text; _copy.IsEnabled = _edit.IsEnabled = true;
            _notice.Text = "Review the plain-text result before copying. Original notes and formatting stay unchanged.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or System.Security.Cryptography.CryptographicException)
        { if (!cancellation.IsCancellationRequested) { _preview.Text = ""; _notice.Text = ex is ArgumentException ? ex.Message : "Some selected notes are unavailable. Select them again or use a smaller group."; } }
        finally { if (ReferenceEquals(_pending, source)) _pending = null; }
    }
}
