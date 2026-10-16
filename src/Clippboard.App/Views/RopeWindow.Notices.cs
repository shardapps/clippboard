using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Clippboard.App.Services;

namespace Clippboard.App.Views;

public partial class RopeWindow
{
    internal void OpenNotices()
    {
        if (Retired) return;
        try
        {
            var text = BundledNotices.Read();
            DismissSettings(); DismissDetail();
            var body = new DockPanel { Margin = new Thickness(24) };
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            var close = IconButton("close", "Close licenses and notices"); close.Click += (_, _) => DismissDetail(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
            header.Children.Add(new TextBlock { Text = "Licenses & notices", FontSize = 23, FontWeight = FontWeights.SemiBold });
            DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
            var viewer = new TextBox { Text = text, IsReadOnly = true, IsUndoEnabled = false, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13 };
            System.Windows.Automation.AutomationProperties.SetName(viewer, "Bundled licenses and third-party notices"); body.Children.Add(viewer);
            DetailHost.Width = Math.Min(820, Math.Max(200, ActualWidth - 48)); DetailHost.Height = Math.Min(620, Math.Max(180, AvailableHeight - 48));
            DetailHost.Child = body; DetailHost.Visibility = Visibility.Visible; Reveal(DetailHost, 12); viewer.Focus();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        { SetStatus("Could not open bundled notices. They are also available in the application's licenses folder and source repository."); }
    }
}
