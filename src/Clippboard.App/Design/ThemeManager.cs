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
            AppTheme.Midnight => new[] { "#FF242833", "#FF2B303C", "#F1F4FC", "#B3BDCF", "#434B5E", "#ABC0FF", "#354360", "#FFC973" },
            AppTheme.Paper => new[] { "#FFFFF9F0", "#FFFFFAF1", "#3C3540", "#786E79", "#E5D9CB", "#A35F72", "#F2DFE5", "#AA6B23" },
            AppTheme.Lavender => new[] { "#FFF8F5FF", "#FFFCFAFF", "#342C48", "#746782", "#DDD4EB", "#7652AF", "#EBE1FA", "#926315" },
            AppTheme.Ocean => new[] { "#FF192E3B", "#FF203846", "#EAF5FC", "#ACC3D0", "#466170", "#9CD5F4", "#314F63", "#FFD28A" },
            AppTheme.Rose => new[] { "#FFFFF5F6", "#FFFFFAFA", "#482F37", "#806571", "#EDD8DE", "#AA4968", "#F9E0E8", "#946514" },
            _ => new[] { "#FFFFFFFF", "#FFFFFFFF", "#273146", "#69758A", "#DDE3ED", "#526DCC", "#E9EEFF", "#A47313" }
        };
        string[] keys = ["Chrome", "NoteSurface", "Foreground", "Muted", "Outline", "Accent", "SelectedSurface", "StarColor"];
        for (var i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])); brush.Freeze(); resources[keys[i]] = brush;
        }
    }
}
