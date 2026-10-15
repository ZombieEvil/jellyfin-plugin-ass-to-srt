using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AssToSrt.Services;

/// <summary>
/// What the cleaner keeps from the ASS styling.
/// </summary>
public sealed class SrtStyleOptions
{
    /// <summary>Gets or sets a value indicating whether italic tags are kept.</summary>
    public bool KeepItalic { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether bold tags are kept.</summary>
    public bool KeepBold { get; set; }

    /// <summary>Gets or sets a value indicating whether underline tags are kept.</summary>
    public bool KeepUnderline { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether text colors are kept.</summary>
    public bool KeepColors { get; set; }

    /// <summary>Gets or sets a value indicating whether {\anN} positions (top of screen...) are kept.</summary>
    public bool KeepPositions { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether each line of dialogue is put on a single line.</summary>
    public bool SingleLine { get; set; }
}

/// <summary>
/// Cleans the SRT produced by ffmpeg from an ASS source so that simple players display it correctly.
/// </summary>
public static partial class SrtCleaner
{
    [GeneratedRegex(@"\r?\n[ \t]*\r?\n")]
    private static partial Regex BlockSeparator();

    [GeneratedRegex(@"<(/?)font([^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex FontTag();

    [GeneratedRegex(@"color\s*=\s*""?(#?[0-9a-fA-F]{6})""?", RegexOptions.IgnoreCase)]
    private static partial Regex ColorAttribute();

    [GeneratedRegex(@"\{\\an\d\}")]
    private static partial Regex PositionTag();

    // ASS override blocks that ffmpeg left behind, except {\anN} positioning.
    [GeneratedRegex(@"\{(?!\\an\d\})[^}]*\}")]
    private static partial Regex AssOverride();

    // Vector drawing commands ("m 0 0 l 100 0 ...") used for signs: unreadable as text.
    [GeneratedRegex(@"^\s*m\s+-?\d+(\.\d+)?\s+-?\d+(\.\d+)?(\s+[mlbspc]\s+[-\d\.\s]+)*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Drawing();

    [GeneratedRegex(@"<[^>]+>|\{[^}]*\}")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"^\s*(<[^>]+>|\{[^}]*\})*\s*[-–—]")]
    private static partial Regex StartsWithDash();

    /// <summary>
    /// Cleans an SRT document.
    /// </summary>
    /// <param name="srt">SRT content produced by ffmpeg.</param>
    /// <param name="options">What to keep from the ASS styling.</param>
    /// <returns>The cleaned SRT, renumbered, with CRLF line endings.</returns>
    public static string Clean(string srt, SrtStyleOptions options)
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

            // Tags can open on one line and close on the next: style the whole cue at once.
            var text = string.Join('\n', lines.Skip(timingIndex + 1).Select(l => l.TrimEnd('\r')));
            var textLines = StyleText(text, options)
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l =>
                {
                    var visible = AnyTag().Replace(l, string.Empty).Trim();
                    return visible.Length > 0 && !Drawing().IsMatch(visible);
                })
                .ToList();

            if (textLines.Count == 0)
            {
                continue;
            }

            if (options.SingleLine)
            {
                textLines = JoinLines(textLines);
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

    private static string StyleText(string text, SrtStyleOptions options)
    {
        text = AssOverride().Replace(text, string.Empty);
        if (!options.KeepPositions)
        {
            text = PositionTag().Replace(text, string.Empty);
        }

        text = RemoveTag(text, "i", options.KeepItalic);
        text = RemoveTag(text, "b", options.KeepBold);
        text = RemoveTag(text, "u", options.KeepUnderline);

        // <font> can be nested: keep only the color, and only close the tags that were kept.
        var kept = new Stack<bool>();
        return FontTag().Replace(text, m =>
        {
            if (m.Groups[1].Value == "/")
            {
                return kept.Count > 0 && kept.Pop() ? "</font>" : string.Empty;
            }

            var color = ColorAttribute().Match(m.Groups[2].Value);
            var keep = options.KeepColors && color.Success;
            kept.Push(keep);
            return keep ? "<font color=\"" + NormalizeColor(color.Groups[1].Value) + "\">" : string.Empty;
        });
    }

    private static string RemoveTag(string text, string tag, bool keep)
        => keep ? text : Regex.Replace(text, "</?" + tag + ">", string.Empty, RegexOptions.IgnoreCase);

    private static string NormalizeColor(string color)
        => color.StartsWith('#') ? color : "#" + color;

    // Dialogue lines starting with a dash stay on their own line.
    private static List<string> JoinLines(List<string> lines)
    {
        var result = new List<string> { lines[0] };
        foreach (var line in lines.Skip(1))
        {
            if (StartsWithDash().IsMatch(line))
            {
                result.Add(line);
            }
            else
            {
                result[^1] = result[^1] + " " + line;
            }
        }

        return result;
    }
}
