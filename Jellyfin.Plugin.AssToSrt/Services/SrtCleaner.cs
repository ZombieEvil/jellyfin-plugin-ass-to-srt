using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AssToSrt.Services;

/// <summary>
/// Cleans the SRT produced by ffmpeg from an ASS source so that simple players display it correctly.
/// </summary>
public static partial class SrtCleaner
{
    [GeneratedRegex(@"\r?\n[ \t]*\r?\n")]
    private static partial Regex BlockSeparator();

    [GeneratedRegex(@"</?font[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FontTag();

    [GeneratedRegex(@"</?[biu]>", RegexOptions.IgnoreCase)]
    private static partial Regex BasicTag();

    // ASS override blocks that ffmpeg left behind, except {\anN} positioning which SRT players understand.
    [GeneratedRegex(@"\{(?!\\an\d\})[^}]*\}")]
    private static partial Regex AssOverride();

    // Vector drawing commands ("m 0 0 l 100 0 ...") used for signs: unreadable as text.
    [GeneratedRegex(@"^\s*m\s+-?\d+(\.\d+)?\s+-?\d+(\.\d+)?(\s+[mlbspc]\s+[-\d\.\s]+)*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Drawing();

    [GeneratedRegex(@"<[^>]+>|\{[^}]*\}")]
    private static partial Regex AnyTag();

    /// <summary>
    /// Cleans an SRT document.
    /// </summary>
    /// <param name="srt">SRT content produced by ffmpeg.</param>
    /// <param name="keepBasicFormatting">Keep &lt;b&gt;, &lt;i&gt;, &lt;u&gt; tags.</param>
    /// <returns>The cleaned SRT, renumbered, with CRLF line endings.</returns>
    public static string Clean(string srt, bool keepBasicFormatting)
    {
        var output = new StringBuilder();
        var number = 0;

        foreach (var block in BlockSeparator().Split(srt.Trim('﻿', '\r', '\n', ' ')))
        {
            var lines = block.Split('\n');
            var timingIndex = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (timingIndex < 0)
            {
                continue;
            }

            var textLines = new List<string>();
            for (var i = timingIndex + 1; i < lines.Length; i++)
            {
                var text = FontTag().Replace(lines[i].TrimEnd('\r'), string.Empty);
                text = AssOverride().Replace(text, string.Empty);
                if (!keepBasicFormatting)
                {
                    text = BasicTag().Replace(text, string.Empty);
                }

                text = text.Trim();
                var visible = AnyTag().Replace(text, string.Empty).Trim();
                if (visible.Length == 0 || Drawing().IsMatch(visible))
                {
                    continue;
                }

                textLines.Add(text);
            }

            if (textLines.Count == 0)
            {
                continue;
            }

            number++;
            output.Append(number.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            output.Append(lines[timingIndex].Trim()).Append("\r\n");
            foreach (var line in textLines)
            {
                output.Append(line).Append("\r\n");
            }

            output.Append("\r\n");
        }

        return output.ToString();
    }
}
