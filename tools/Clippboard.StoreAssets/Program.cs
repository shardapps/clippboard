using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clippboard.App.Views;
using Clippboard.Core;

// Marketing fixtures only: the actual beta views, an in-memory history and a
// labelled sample workspace. No clipboard listener, real history or restore.
internal static class Program
{
    private const int Width = 1600, Height = 900;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Expected output directory and wallpaper path."); return 2; }
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var background = Load(Path.GetFullPath(args[1]));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clippboard;component/Design/Controls.xaml", UriKind.Relative) });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var exit = 0;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RenderAll(output, background); }
            catch (Exception error) { Console.Error.WriteLine(error); exit = 1; }
            finally { app.Shutdown(); }
        }));
        app.Run(); return exit;
    }
    private static BitmapImage Load(string path)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); return image;
    }
    private static object? Preview(RopeWindow rope, string name, params object?[] args)
        => (typeof(RopeWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(name)).Invoke(rope, args);
    private static async Task RenderAll(string output, BitmapSource background)
    {
        var cases = new[] { ("01-clipboard-on-your-desktop", AppTheme.Cloud, "overview"), ("02-find-a-copied-idea", AppTheme.Midnight, "search"),
            ("03-words-you-reuse", AppTheme.Paper, "snippet"), ("04-make-it-yours", AppTheme.Lavender, "settings"),
            ("05-encrypted-backups", AppTheme.Ocean, "backup"), ("06-text-from-images", AppTheme.Rose, "ocr") };
        foreach (var (name, theme, page) in cases)
        {
            var history = new Samples(background);
            var rope = new RopeWindow(history, preview: true, settings: new AppSettings { Theme = theme, Animate = false })
                { Width = Width, Height = Height, Left = -20000, Top = -20000, ShowActivated = false, Topmost = false, ShowInTaskbar = false };
            try
            {
                var scene = (Grid)rope.FindName("Scene");
                scene.Children.Insert(0, Workspace(background));
                rope.Show(); Preview(rope, "RenderDemo", 8); await rope.RefreshAsync();
                switch (page)
                {
                    case "search": await (Task)Preview(rope, "SearchNowAsync", "launch")!; break;
                    case "snippet": await (Task)Preview(rope, "OpenTextEditorAsync", history.Snippet.Summary(0), null)!; break;
                    case "settings": Preview(rope, "OpenSettings", 0); break;
                    case "backup": Preview(rope, "PreviewBackup"); break;
                    case "ocr": await (Task)Preview(rope, "PreviewOcrAsync")!; break;
                }
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(100); rope.UpdateLayout();
                var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
                // Native DIP/device rounding can leave a fractional edge. An
                // opaque capture canvas keeps that edge out of Store assets.
                var canvas = new DrawingVisual();
                using (var context = canvas.RenderOpen())
                {
                    context.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
                    context.DrawRectangle(new VisualBrush(scene) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, Width, Height), Stretch = Stretch.Fill }, null, new Rect(0, 0, Width, Height));
                }
                bitmap.Render(canvas);
                // Store screenshots are opaque RGB exports, not a desktop
                // overlay PNG whose fractional alpha gets a black backdrop.
                var opaque = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(opaque));
                var path = Path.Combine(output, name + ".png"); using (var file = File.Create(path)) png.Save(file);
                Verify(path); Console.WriteLine("Rendered actual beta views: " + name);
            }
            finally { rope.Close(); }
        }
        Console.WriteLine("PASS: six opaque 1600x900 PNGs, synthetic in-memory fixtures, no personal clipboard/history reads.");
    }
    private static Grid Workspace(BitmapSource background)
    {
        var grid = new Grid { Background = new ImageBrush(background) { Stretch = Stretch.UniformToFill }, IsHitTestVisible = false };
        var editor = new Border { Width = 1080, Height = 270, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 32), CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(Color.FromArgb(242, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(207, 209, 216)), BorderThickness = new Thickness(1), Padding = new Thickness(28, 20, 28, 20) };
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "Sample workspace  /  Launch notes", FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(100, 104, 118)), Margin = new Thickness(0, 0, 0, 14) });
        content.Children.Add(new TextBlock { Text = "Project launch notes", FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(38, 42, 56)), Margin = new Thickness(0, 0, 0, 10) });
        content.Children.Add(new TextBox { Text = "Launch checklist\nReview the outline, gather the visuals, and send a friendly follow-up.\n\nNext step: add the research notes here.", FontSize = 17, IsReadOnly = true, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(Color.FromRgb(72, 77, 91)), TextWrapping = TextWrapping.Wrap });
        editor.Child = content; grid.Children.Add(editor); return grid;
    }
    private static void Verify(string path)
    {
        using var stream = File.OpenRead(path); var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (image.PixelWidth != Width || image.PixelHeight != Height || stream.Length >= 50 * 1024 * 1024) throw new InvalidDataException("Invalid Store screenshot dimensions/size.");
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); var pixels = new byte[Width * Height * 4]; converted.CopyPixels(pixels, Width * 4, 0);
        for (var index = 3; index < pixels.Length; index += 4) if (pixels[index] != 255) throw new InvalidDataException("Screenshot contains transparent pixels.");
    }
    private sealed class Samples : IHistoryAccess
    {
        private readonly List<Clip> _clips;
        internal Clip Snippet => _clips.First(clip => clip.IsSnippet);
        internal Samples(BitmapSource background)
        {
            var thumbnail = new TransformedBitmap(background, new ScaleTransform(360.0 / background.PixelWidth, 220.0 / background.PixelHeight));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(thumbnail)); using var bytes = new MemoryStream(); encoder.Save(bytes);
            _clips = [new() { Kind = ClipKind.Text, Text = "Launch checklist\n\nReview the outline\nGather the visuals\nSend the follow-up", Pinned = true, Labels = ["project"] },
                new() { Kind = ClipKind.Image, ImagePng = bytes.ToArray(), Labels = ["inspiration"] },
                new() { Kind = ClipKind.Files, FilePaths = [@"C:\Sample workspace\Launch plan.pdf", @"C:\Sample workspace\Research notes.txt"] },
                new() { Kind = ClipKind.Text, Text = "Hi there,\n\nThanks for sharing your ideas. I’ll review the details and get back to you tomorrow.\n\nBest wishes", Title = "A friendly follow-up", IsSnippet = true, Labels = ["email", "work"] },
                new() { Kind = ClipKind.Text, Text = "Design review\nTuesday, 10:30\n\nBring the updated colour palette and the first launch outline.", Labels = ["project"] },
                new() { Kind = ClipKind.Text, Text = "Project reference\nhttps://example.com/research\n\nA useful link for later.", Labels = ["research"] },
                new() { Kind = ClipKind.Text, Text = "Weekend ideas\nVisit the bookshop\nTake the scenic route\nMake time for a small project" },
                new() { Kind = ClipKind.Text, Text = "Hello — مرحبا — 你好\n\nA little room for words from everywhere." }];
            for (var index = 0; index < _clips.Count; ++index) _clips[index] = _clips[index] with { CopiedAt = new DateTimeOffset(2026, 10, 19, 9, 20, 0, TimeSpan.Zero).AddMinutes(-index * 3) };
        }
        public Task<IReadOnlyList<ClipSummary>> ListAsync() => Task.FromResult<IReadOnlyList<ClipSummary>>(_clips.Select(clip => clip.Summary(0)).ToArray());
        public Task<Clip?> GetAsync(string id) { var clip = _clips.Find(item => item.Id == id); return Task.FromResult(clip is null ? null : clip with { ImagePng = clip.ImagePng?.ToArray() }); }
        public Task SetPinnedAsync(string id, bool pinned) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }
}
