using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Clippboard.Core;
using Clippboard.App.Platform;
using Clippboard.App.Services;

internal static partial class Program
{
    internal static string HtmlFixture(string fragment)
    {
        const string template = "Version:1.0\r\nStartHTML:{0:0000000000}\r\nEndHTML:{1:0000000000}\r\nStartFragment:{2:0000000000}\r\nEndFragment:{3:0000000000}\r\n";
        const string prefix = "<html><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, 0, 0, 0, 0));
        var start = headerLength + Encoding.UTF8.GetByteCount(prefix); var end = start + Encoding.UTF8.GetByteCount(fragment);
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, headerLength, end + Encoding.UTF8.GetByteCount(suffix), start, end) + prefix + fragment + suffix;
    }
    private static async Task CheckRichTextAsync(string root)
    {
        var html = HtmlFixture("<b>اردو 🧵</b><img src='https://invalid.example/inert'>");
        const string rtf = "{\\rtf1\\ansi Hello \\b world\\b0}";
        Check(RichTextFormats.ValidHtml(html) && RichTextFormats.ValidRtf(rtf), "bounded HTML uses UTF-8 byte offsets and RTF is preserved");
        var htmlStart = html.IndexOf("StartHTML:", StringComparison.Ordinal) + 10;
        Check(!RichTextFormats.ValidHtml(html[..htmlStart] + "2147483647" + html[(htmlStart + 10)..])
            && !RichTextFormats.ValidHtml("Version:1.0\r\nStartHTML:2147483647\r\n"), "overflowing or incomplete HTML offsets fail closed");
        var fragmentStart = html.IndexOf("StartFragment:", StringComparison.Ordinal) + "StartFragment:".Length;
        var invalid = html[..fragmentStart] + "2147483647" + html[(fragmentStart + 10)..];
        Check(!RichTextFormats.ValidHtml(invalid), "HTML fragment outside original bytes is rejected");
        var unicode = HtmlFixture("🧵"); var offset = unicode.IndexOf("StartFragment:", StringComparison.Ordinal) + 14;
        var byteStart = int.Parse(unicode.AsSpan(offset, 10), System.Globalization.CultureInfo.InvariantCulture);
        Check(!RichTextFormats.ValidHtml(unicode[..offset] + (byteStart + 1).ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + unicode[(offset + 10)..]), "HTML offsets cannot split a UTF-8 code point");
        Check(!RichTextFormats.ValidRtf("{\\rtf1 " + new string('語', RichTextFormats.MaxFormatBytes / 2) + "}")
            && !RichTextFormats.ValidRtf("{\\rtf1 broken"), "RTF byte bounds and closing group are enforced");
        var clip = new Clip { Kind = ClipKind.Text, Text = "اردو 🧵", Html = html, Rtf = rtf };
        Check(clip.Fingerprint() != (clip with { Html = null, Rtf = null }).Fingerprint()
            && clip.Fingerprint() != (clip with { Rtf = "{\\rtf1 other}" }).Fingerprint(), "deduplication distinguishes source formatting and plaintext");
        var path = Path.Combine(root, "rich-text"); var history = new HistoryStore(path, new WindowsProtector()); await history.InitializeAsync();
        Check(await history.AddAsync(clip) && File.Exists(Path.Combine(path, clip.Id + ".clip2")), "rich payload is encrypted in downgrade-safe modern storage");
        var restart = new HistoryStore(path, new WindowsProtector()); await restart.InitializeAsync();
        var read = await restart.GetAsync(clip.Id); var summary = (await restart.ListAsync()).Single();
        Check(read?.Html == html && read.Rtf == rtf && summary.HasRichText && summary.Preview == clip.Text, "restart preserves formats while summary retains only plain text");
        await restart.SetPinnedAsync(clip.Id, true);
        Check((await restart.GetAsync(clip.Id))!.Html == html, "starring a formatted note preserves source formats");
        await restart.AddAsync(clip with { Id = Guid.NewGuid().ToString("N"), Html = null, Rtf = null });
        Check((await restart.ListAsync()).Count == 2, "plain and formatted copies remain independently available");
        var rejected = false;
        try { await restart.AddAsync(clip with { Html = "bad" }); } catch (ArgumentException) { rejected = true; }
        Check(rejected && (await restart.GetAsync(clip.Id))!.Html == html, "invalid formats cannot replace an intact saved note");
        Check(await restart.SaveSnippetAsync(null, clip.Text!, "Plain snippet", [])
            && !(await restart.GetAsync((await restart.ListAsync()).Single(x => x.IsSnippet).Id))!.HasRichText, "snippet conversion intentionally retains only reviewed plain text");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var data = FormattedClipboard.ForText(clip);
                var captured = ClipboardService.ReadData(data, () => null);
                Check(captured?.Text == clip.Text && captured.Html == html && captured.Rtf == rtf, "synthetic clipboard round trip restores rich formats alongside Unicode fallback");
                Check(ClipboardService.ReadData(data, () => null, new AppSettings { CaptureFormattedText = false }) is { HasRichText: false }, "format capture can be disabled without disabling text");
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html + '\0')); stream.Position = 4;
                var streamed = new DataObject(); streamed.SetText("fallback"); streamed.SetData(DataFormats.Html, stream);
                Check(ClipboardService.ReadData(streamed, () => null)?.Html == html && stream.Position == 4 && stream.CanRead, "bounded stream formats preserve source ownership and position");
                streamed.SetData(DataFormats.Html, invalid); streamed.SetData(DataFormats.Rtf, "bad");
                Check(ClipboardService.ReadData(streamed, () => null) is { Text: "fallback", HasRichText: false }, "malformed optional formats fall back to plaintext");
                var richOnly = new DataObject(); richOnly.SetData(DataFormats.Html, html, false);
                Check(ClipboardService.ReadData(richOnly, () => null) is null, "rich-only data is not rendered to synthesize a fallback");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
}
