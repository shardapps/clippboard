using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Clippboard.Core;

namespace Clippboard.App.Views;

internal sealed class TextEditorView : UserControl
{
    private readonly string _original;
    private readonly TextBox _text, _title, _labels;
    private readonly TextBlock _notice = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly SemaphoreSlim _work = new(1, 1);
    private CancellationTokenSource? _transform;
    public event Action? Dismissed;
    public event Func<string, Task>? CopyRequested;
    public event Func<string, string, string[], bool, Task<string?>>? SaveRequested;
    internal string PreviewText => _text.Text;

    internal TextEditorView(string original, string title, string[] labels, bool existingSnippet, bool animate)
    {
        _original = original;
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = RopeWindow.IconButton("close", "Close text editor"); close.Click += (_, _) => Dismissed?.Invoke(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = existingSnippet ? "Edit your snippet" : original.Length == 0 ? "New reusable snippet" : "Work with a copy", FontSize = 23, FontWeight = FontWeights.SemiBold }); root.Children.Add(header);
        var options = new StackPanel();
        _title = new TextBox { Text = title, MaxLength = 64, Margin = new Thickness(0, 0, 0, 6), ToolTip = "Snippet title (optional)" };
        _labels = new TextBox { Text = string.Join(", ", labels), MaxLength = 208, ToolTip = "Up to eight comma-separated labels" };
        System.Windows.Automation.AutomationProperties.SetName(_title, "Snippet title"); System.Windows.Automation.AutomationProperties.SetName(_labels, "Comma-separated snippet labels");
        options.Children.Add(new TextBlock { Text = "Title (optional)", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) }); options.Children.Add(_title);
        options.Children.Add(new TextBlock { Text = "Labels (comma-separated)", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) }); options.Children.Add(_labels);
        var transforms = new WrapPanel { Margin = new Thickness(0, 8, 0, 10) };
        foreach (var transform in Enum.GetValues<TextTransform>())
        {
            var action = new Button { Content = transform switch { TextTransform.Original => "Original", TextTransform.TrimEdges => "Trim", TextTransform.Uppercase => "UPPER", TextTransform.Lowercase => "lower", TextTransform.CollapseWhitespace => "Whitespace", TextTransform.SortLines => "Sort lines", _ => "Unique lines" }, Margin = new Thickness(0, 2, 3, 2) };
            action.Click += async (_, _) => await TransformAsync(transform); transforms.Children.Add(action);
        }
        options.Children.Add(transforms); Grid.SetRow(options, 1); root.Children.Add(options);
        _text = new TextBox { Text = original, MaxLength = TextReuse.MaxTextLength, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 17 };
        _text.TextChanged += (_, _) => _transform?.Cancel();
        System.Windows.Automation.AutomationProperties.SetName(_text, "Editable text preview; original clipboard note is preserved"); Grid.SetRow(_text, 2); root.Children.Add(_text);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var actions = new WrapPanel(); var copy = new Button { Content = "Copy preview" };
        copy.Click += async (_, _) => { if (CopyRequested is not null) await CopyRequested(_text.Text); }; actions.Children.Add(copy);
        var save = new Button { Content = existingSnippet ? "Save snippet changes" : "Save as snippet" }; save.SetResourceReference(BackgroundProperty, "SelectedSurface");
        save.Click += async (_, _) => await SaveAsync(!existingSnippet); actions.Children.Add(save);
        if (existingSnippet) { var duplicate = new Button { Content = "Save new copy" }; duplicate.Click += async (_, _) => await SaveAsync(true); actions.Children.Add(duplicate); }
        footer.Children.Add(actions); footer.Children.Add(_notice);
        SetNotice("Preview changes before copying or saving. Original notes stay unchanged. Snippets still expire within 30 days.");
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        Unloaded += (_, _) => { _transform?.Cancel(); _transform?.Dispose(); _transform = null; };
        Clippboard.App.Design.Interaction.SetEnabled(this, animate);
    }
    internal void FocusText() => _text.Focus();
    internal void SetNotice(string message) => _notice.Text = message;
    internal async Task TransformAsync(TextTransform transform)
    {
        _transform?.Cancel(); _transform?.Dispose(); using var source = new CancellationTokenSource(); _transform = source;
        var cancellation = source.Token; var input = transform == TextTransform.Original ? _original : _text.Text;
        try
        {
            await _work.WaitAsync(cancellation);
            string result;
            try { result = await Task.Run(() => TextReuse.Transform(input, transform), cancellation); }
            finally { _work.Release(); }
            cancellation.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_transform, source)) return;
            _text.Text = result; SetNotice("Preview updated. Copy or Save when ready.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        { if (!cancellation.IsCancellationRequested) SetNotice("Could not transform this text. Shorten it or edit it directly."); }
        finally { if (ReferenceEquals(_transform, source)) _transform = null; }
    }
    private async Task SaveAsync(bool newCopy)
    {
        IsEnabled = false;
        try
        {
            var labels = TextReuse.Labels(_labels.Text); TextReuse.ValidateSnippet(_text.Text, _title.Text, labels);
            var error = SaveRequested is null ? "Saving is unavailable." : await SaveRequested(_text.Text, _title.Text, labels, newCopy);
            SetNotice(error ?? "Snippet saved; follows your retention limit.");
        }
        catch (ArgumentException ex) { SetNotice(ex.Message); }
        finally { IsEnabled = true; }
    }
}
