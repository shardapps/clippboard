using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Clippboard.Core;

namespace Clippboard.App.Services;

internal static class FormattedClipboard
{
    internal static string? Read(IDataObject data, string format)
    {
        try
        {
            if (!data.GetDataPresent(format, false)) return null;
            var value = data.GetData(format, false);
            string? text = value as string;
            if (value is Stream stream && stream.CanSeek && stream.CanRead)
            {
                // The source owns this stream. Bound before allocation and put its
                // cursor back; do not dispose an OLE object's shared stream.
                var position = stream.Position;
                if (stream.Length is <= 0 or > RichTextFormats.MaxFormatBytes + 1) return null;
                var bytes = new byte[(int)stream.Length];
                try { stream.Position = 0; stream.ReadExactly(bytes); text = new UTF8Encoding(false, true).GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); stream.Position = position; }
            }
            if (text is null || text.Length > RichTextFormats.MaxFormatBytes + 1) return null;
            text = text.TrimEnd('\0');
            return (format == DataFormats.Html ? RichTextFormats.ValidHtml(text) : RichTextFormats.ValidRtf(text)) ? text : null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or COMException)
        { return null; } // A broken optional rich format never loses valid plaintext.
    }
    internal static DataObject ForText(Clip clip)
    {
        var data = new DataObject(); data.SetText(clip.Text ?? "", TextDataFormat.UnicodeText);
        if (RichTextFormats.ValidHtml(clip.Html)) data.SetText(clip.Html!, TextDataFormat.Html);
        if (RichTextFormats.ValidRtf(clip.Rtf)) data.SetText(clip.Rtf!, TextDataFormat.Rtf);
        return data;
    }
}
