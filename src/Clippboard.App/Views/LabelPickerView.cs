using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clippboard.Core;

namespace Clippboard.App.Views;

internal sealed class LabelPickerView : UserControl
{
    private readonly TextBox _search = new() { MaxLength = 100, Margin = new Thickness(0, 12, 0, 10), ToolTip = "Find a saved label" };
    private readonly ListBox _list = new() { MinHeight = 100, MaxHeight = 300, BorderThickness = new Thickness(0) };
    public event Action<string?>? Chosen;
    public event Action? Dismissed;
    internal LabelPickerView(IReadOnlyList<string> labels, string? selected)
    {
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        var header = new DockPanel(); var close = RopeWindow.IconButton("close", "Close label picker"); close.Click += (_, _) => Dismissed?.Invoke(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "Find a label", FontSize = 23, FontWeight = FontWeights.SemiBold }); root.Children.Add(header);
        var all = new Button { Content = "All labels", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        all.Click += (_, _) => Chosen?.Invoke(null); Grid.SetRow(all, 1); root.Children.Add(all);
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search saved labels"); Grid.SetRow(_search, 2); root.Children.Add(_search);
        _list.SetResourceReference(BackgroundProperty, "Chrome"); _list.SetResourceReference(ForegroundProperty, "Foreground");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled); ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(_list, true); VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        _list.ItemContainerStyle = ChoiceItemStyle();
        _list.ItemsSource = labels; _list.SelectedItem = selected;
        System.Windows.Automation.AutomationProperties.SetName(_list, "Saved labels; press Enter to apply selection");
        _list.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && _list.SelectedItem is string label) { Chosen?.Invoke(label); e.Handled = true; } };
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        { if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(_list, source) is ListBoxItem item && item.Content is string label) Chosen?.Invoke(label); };
        _search.TextChanged += (_, _) => _list.ItemsSource = labels.Where(label => HistorySearch.Contains(label, _search.Text.Trim())).ToArray();
        _search.PreviewKeyDown += (_, e) => { if (e.Key == Key.Down && _list.Items.Count > 0) { _list.SelectedIndex = 0; _list.Focus(); e.Handled = true; } };
        Grid.SetRow(_list, 3); root.Children.Add(_list); Content = root;
    }
    internal static Style ChoiceItemStyle()
    {
        var style = new Style(typeof(ListBoxItem)); style.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 9, 12, 9)));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(BorderBrushProperty, System.Windows.Media.Brushes.Transparent));
        style.Setters.Add(new Setter(ForegroundProperty, new System.Windows.DynamicResourceExtension("Foreground")));
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty)); border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty)); border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter))); var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; selectedTrigger.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("SelectedSurface"))); template.Triggers.Add(selectedTrigger);
        selectedTrigger.Setters.Add(new Setter(BorderBrushProperty, new DynamicResourceExtension("Accent")));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("SelectedSurface"))); template.Triggers.Add(hover);
        style.Setters.Add(new Setter(TemplateProperty, template)); return style;
    }
    internal void FocusSearch() => _search.Focus();
}
