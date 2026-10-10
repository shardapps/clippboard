using System.Windows;
using System.Windows.Media;
using Clippboard.Core;

namespace Clippboard.App.Design;

internal static class ThemeManager
{
    public static void Apply(AppTheme theme, ResourceDictionary resources)
    {
        var colors = theme switch
        {
            AppTheme.Midnight => new[] { "#F2242833", "#FF2B303C", "#F1F4FC", "#B3BDCF", "#434B5E", "#ABC0FF", "#354360", "#FFC973" },
            AppTheme.Paper => new[] { "#F5FFF9F0", "#FFFFFAF1", "#3C3540", "#786E79", "#E5D9CB", "#A35F72", "#F2DFE5", "#AA6B23" },
            _ => new[] { "#F5FFFFFF", "#FFFFFFFF", "#273146", "#69758A", "#DDE3ED", "#526DCC", "#E9EEFF", "#A47313" }
        };
        string[] keys = ["Chrome", "NoteSurface", "Foreground", "Muted", "Outline", "Accent", "SelectedSurface", "StarColor"];
        for (var i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])); brush.Freeze(); resources[keys[i]] = brush;
        }
    }
}
