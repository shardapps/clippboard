using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clippboard.Core;

namespace Clippboard.App.Views;

internal sealed class SettingsView : UserControl
{
    private AppSettings _draft;
    private readonly bool _startupAvailable;
    private readonly StackPanel _content = new() { Margin = new Thickness(22, 8, 22, 24) };
    private readonly TextBlock _notice = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _save = new() { Content = "Save changes", Padding = new Thickness(17, 10, 17, 10) };
    private readonly Button[] _tabs = new Button[4];
    private int _page;
    public event Func<AppSettings, Task<string?>>? SaveRequested;
    public event Action<AppTheme>? ThemePreview;
    public event Action? Dismissed;
    public event Func<Task>? ClearRequested;
    internal AppSettings Draft => _draft;

    public SettingsView(AppSettings settings, bool startupAvailable)
    {
        _draft = settings; _startupAvailable = startupAvailable;
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(5, 0, 0, 18) };
        var close = RopeWindow.IconButton("close", "Close settings"); close.Click += (_, _) => Dismissed?.Invoke(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "Make it yours.", FontSize = 25, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }); root.Children.Add(header);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(125) }); body.ColumnDefinitions.Add(new());
        var rail = new StackPanel(); string[] names = ["Themes", "Behaviour", "Storage", "Capture"];
        for (var i = 0; i < names.Length; i++)
        {
            var page = i; var tab = new Button { Content = names[i], Margin = new Thickness(0, 0, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(13, 11, 13, 11) };
            tab.Click += (_, _) => ShowPage(page); _tabs[i] = tab; rail.Children.Add(tab);
        }
        body.Children.Add(rail);
        var scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1); body.Children.Add(scroll); Grid.SetRow(body, 1); root.Children.Add(body);
        var footer = new StackPanel { Margin = new Thickness(4, 15, 4, 0) };
        var actions = new DockPanel();
        _save.SetResourceReference(BackgroundProperty, "SelectedSurface"); _save.Click += Save;
        DockPanel.SetDock(_save, Dock.Right); actions.Children.Add(_save);
        var reset = new Button { Content = "Reset defaults", HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => { _draft = new(); ThemePreview?.Invoke(_draft.Theme); ShowPage(_page); _notice.Text = "Defaults selected. Save to apply them."; };
        actions.Children.Add(reset); footer.Children.Add(actions); footer.Children.Add(_notice); Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root; ShowPage(0);
    }
    internal void ShowPage(int page)
    {
        _page = page; _content.Children.Clear(); _notice.Text = "";
        for (var i = 0; i < _tabs.Length; i++) _tabs[i].SetResourceReference(BackgroundProperty, i == page ? "SelectedSurface" : "Chrome");
        if (page == 0)
        {
            Heading("A calmer clipboard", "Choose your paper, then give every note room to breathe.");
            foreach (var theme in Enum.GetValues<AppTheme>()) ThemeCard(theme);
            Number("Note width", _draft.NoteWidth, 250, 400, "px", v => _draft = _draft with { NoteWidth = v });
            Number("Space between notes", _draft.NoteSpacing, 24, 96, "px", v => _draft = _draft with { NoteSpacing = v });
            Number("Text size", _draft.TextSize, 15, 22, "pt", v => _draft = _draft with { TextSize = v });
        }
        else if (page == 1)
        {
            Heading("Your rhythm", "Opening, finding and restoring things should feel effortless.");
            Toggle("Smooth transitions", _draft.Animate, v => _draft = _draft with { Animate = v });
            Toggle("Gentle rope bounce & note sway", _draft.PlayfulMotion, v => _draft = _draft with { PlayfulMotion = v });
            Hint("Windows reduced-motion preferences are always respected.");
            Toggle("Paste into the previous app", _draft.AutoPaste, v => _draft = _draft with { AutoPaste = v });
            Hint("Turn off to restore to the clipboard only. Shift-click also copies without pasting.");
            Toggle("Show starred notes first", _draft.StarredFirst, v => _draft = _draft with { StarredFirst = v });
            Toggle("Open on the starred list", _draft.OpenStarredOnly, v => _draft = _draft with { OpenStarredOnly = v });
            var shortcut = new Button { Content = "Shortcut: " + ShortcutLabel(_draft.Shortcut), Margin = new Thickness(0, 14, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Left };
            shortcut.Click += (_, _) => { _draft = _draft with { Shortcut = (ShortcutChoice)(((int)_draft.Shortcut + 1) % 3) }; shortcut.Content = "Shortcut: " + ShortcutLabel(_draft.Shortcut); };
            _content.Children.Add(shortcut); Hint("Click to cycle shortcuts. A shortcut used by another app will be rejected when saved.");
            if (_startupAvailable) Toggle("Start at Windows sign-in", _draft.StartAtLogin, v => _draft = _draft with { StartAtLogin = v });
        }
        else if (page == 2)
        {
            Heading("Keep what you need", "History stays encrypted on this PC. Files keep their original locations.");
            Number("Keep history", _draft.RetentionDays, 1, 30, "days", v => _draft = _draft with { RetentionDays = v });
            Hint("30 days is the maximum, including starred notes.");
            Number("Maximum notes", _draft.MaxEntries, 50, 1000, "notes", v => _draft = _draft with { MaxEntries = v });
            Number("History on disk", _draft.StorageMiB, 32, 512, "MiB", v => _draft = _draft with { StorageMiB = v });
            Number("Image preview cache", _draft.PreviewCacheMiB, 4, 64, "MiB", v => _draft = _draft with { PreviewCacheMiB = v });
            Hint("Only small pointers remain in memory while hidden. Images load when needed and the preview cache clears on close. Lower limits can remove older unstarred notes.");
            var clear = new Button { Content = "Clear clipboard history…", Margin = new Thickness(0, 18, 0, 0) }; var confirmed = false;
            clear.Click += async (_, _) =>
            {
                if (!confirmed) { confirmed = true; clear.Content = "Delete all notes, including starred? Click to confirm"; return; }
                clear.IsEnabled = false;
                try { if (ClearRequested is not null) await ClearRequested(); _notice.Text = "History cleared."; }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { _notice.Text = "Could not clear history. Try again."; }
                finally { confirmed = false; clear.Content = "Clear clipboard history…"; clear.IsEnabled = true; }
            };
            _content.Children.Add(clear);
        }
        else
        {
            Heading("Choose what to collect", "Capture runs only while Clippboard is running. You can pause at any time.");
            Toggle("Capture text", _draft.CaptureText, v => _draft = _draft with { CaptureText = v });
            Toggle("Capture images", _draft.CaptureImages, v => _draft = _draft with { CaptureImages = v });
            Toggle("Capture file locations", _draft.CaptureFiles, v => _draft = _draft with { CaptureFiles = v });
            Toggle("Pause all capture", _draft.CapturePaused, v => _draft = _draft with { CapturePaused = v });
            Hint("Items marked private by the source app are ignored. Moved or deleted files must be copied again. Unsupported formats and oversized items are skipped.");
        }
    }
    private async void Save(object sender, RoutedEventArgs e)
    {
        IsEnabled = false; _save.Content = "Saving…";
        try
        {
            var error = SaveRequested is null ? "Settings are unavailable." : await SaveRequested(_draft.Normalize());
            _notice.Text = error ?? "Saved. Your next copy and opening use these settings.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        { _notice.Text = "Could not save settings. Check permissions and try again."; }
        finally { IsEnabled = true; _save.Content = "Save changes"; }
    }
    public static string ShortcutLabel(ShortcutChoice shortcut) => shortcut switch { ShortcutChoice.AltV => "Alt + V", ShortcutChoice.CtrlAltV => "Ctrl + Alt + V", _ => "Ctrl + Shift + V" };
    private void Heading(string title, string hint) { _content.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold }); Hint(hint); }
    private void Hint(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 14) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); _content.Children.Add(label);
    }
    private void Toggle(string text, bool initial, Action<bool> update)
    {
        var toggle = new CheckBox { Content = text, IsChecked = initial, Margin = new Thickness(0, 10, 0, 10), FontSize = 14 };
        toggle.Checked += (_, _) => update(true); toggle.Unchecked += (_, _) => update(false); _content.Children.Add(toggle);
    }
    private void Number(string name, int initial, int min, int max, string unit, Action<int> update)
    {
        var label = new TextBlock { Text = $"{name} · {initial} {unit}", FontSize = 14, Margin = new Thickness(0, 14, 0, 6) };
        var slider = new Slider { Minimum = min, Maximum = max, Value = initial, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 0, 0, 8), Height = 24 };
        System.Windows.Automation.AutomationProperties.SetName(slider, name);
        slider.ValueChanged += (_, _) => { var value = (int)Math.Round(slider.Value); update(value); label.Text = $"{name} · {value} {unit}"; };
        _content.Children.Add(label); _content.Children.Add(slider);
    }
    private void ThemeCard(AppTheme theme)
    {
        string[] fills = ["#FFFFFF", "#FFF8E9", "#282C38"]; string[] inks = ["#526DCC", "#AF745F", "#BAC6FF"];
        var button = new Button { Margin = new Thickness(0, 5, 0, 5), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12) };
        button.SetResourceReference(BackgroundProperty, _draft.Theme == theme ? "SelectedSurface" : "Chrome");
        var row = new DockPanel();
        var mini = new Border { Width = 58, Height = 43, CornerRadius = new CornerRadius(9), Background = (Brush)new BrushConverter().ConvertFromString(fills[(int)theme])!, BorderBrush = (Brush)new BrushConverter().ConvertFromString(inks[(int)theme])!, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 14, 0), Child = new TextBlock { Text = "Aa", FontSize = 21, Foreground = (Brush)new BrushConverter().ConvertFromString(inks[(int)theme])!, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        DockPanel.SetDock(mini, Dock.Left); row.Children.Add(mini);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; labels.Children.Add(new TextBlock { Text = theme.ToString(), FontSize = 15, FontWeight = FontWeights.SemiBold });
        labels.Children.Add(new TextBlock { Text = theme switch { AppTheme.Cloud => "White paper. Clear space.", AppTheme.Paper => "Warm cream. A softer mood.", _ => "Quiet charcoal. Gentle contrast." }, FontSize = 12 }); row.Children.Add(labels); button.Content = row;
        button.Click += (_, _) => { _draft = _draft with { Theme = theme }; ThemePreview?.Invoke(theme); ShowPage(0); }; _content.Children.Add(button);
    }
}
