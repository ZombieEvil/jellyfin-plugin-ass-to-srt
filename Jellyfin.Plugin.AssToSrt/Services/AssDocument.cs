using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AssToSrt.Services;

/// <summary>
/// Vertical area of the screen a subtitle is displayed in.
/// </summary>
public enum ScreenRegion
{
    /// <summary>Bottom of the screen (usual dialogue).</summary>
    Bottom,

    /// <summary>Middle of the screen.</summary>
    Middle,

    /// <summary>Top of the screen.</summary>
    Top
}

/// <summary>
/// What an ASS event is used for.
/// </summary>
public enum EventKind
{
    /// <summary>Spoken dialogue.</summary>
    Dialogue,

    /// <summary>Translation of on-screen text (signs, titles, messages...).</summary>
    Sign,

    /// <summary>Song lyrics (opening, ending, insert songs, karaoke).</summary>
    Song
}

/// <summary>
/// A piece of text with the same formatting.
/// </summary>
/// <param name="Text">The text.</param>
/// <param name="Italic">Italic.</param>
/// <param name="Bold">Bold.</param>
/// <param name="Underline">Underline.</param>
/// <param name="Color">Color as #RRGGBB.</param>
public sealed record TextRun(string Text, bool Italic, bool Bold, bool Underline, string Color);

/// <summary>
/// A displayable ASS event.
/// </summary>
public sealed class SubtitleEvent
{
    /// <summary>Gets or sets the start time in milliseconds.</summary>
    public long Start { get; set; }

    /// <summary>Gets or sets the end time in milliseconds.</summary>
    public long End { get; set; }

    /// <summary>Gets or sets the order of the event in the file.</summary>
    public int Order { get; set; }

    /// <summary>Gets or sets the kind of event.</summary>
    public EventKind Kind { get; set; }

    /// <summary>Gets or sets the screen region of the original placement.</summary>
    public ScreenRegion Region { get; set; }

    /// <summary>Gets the lines of text.</summary>
    public List<List<TextRun>> Lines { get; } = new();

    /// <summary>Gets the text without formatting, used to detect duplicates.</summary>
    public string PlainText => string.Join("\n", Lines.Select(l => string.Concat(l.Select(r => r.Text)).Trim()));
}

/// <summary>
/// Minimal ASS / SSA reader: styles, events and the override tags that matter for SRT.
/// </summary>
public static partial class AssDocument
{
    private const string White = "#FFFFFF";

    [GeneratedRegex(@"(^|[^a-z])(op|ed|opening|ending|songs?|karaok[eé]|kara|lyrics?|chansons?|g[eé]n[eé]rique|romaji|kanji|insert ?song)([^a-z]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SongName();

    [GeneratedRegex(@"sign|title|titre|screen|[ée]cran|panneau|caption|board|card|logo|typeset|\bts\b|\bnote\b|message", RegexOptions.IgnoreCase)]
    private static partial Regex SignName();

    [GeneratedRegex(@"(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)")]
    private static partial Regex Coordinates();

    /// <summary>
    /// Reads an ASS or SSA document.
    /// </summary>
    /// <param name="content">The file content.</param>
    /// <returns>The displayable events, in file order.</returns>
    public static List<SubtitleEvent> Parse(string content)
    {
        var styles = new Dictionary<string, Style>(StringComparer.OrdinalIgnoreCase);
        var events = new List<SubtitleEvent>();
        var formats = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        var isSsa = false;
        var playResY = 0.0;
        var playResX = 0.0;
        var wrapStyle = 0;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line.ToLowerInvariant();
                isSsa |= section == "[v4 styles]";
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (section == "[script info]")
            {
                if (key.Equals("PlayResY", StringComparison.OrdinalIgnoreCase))
                {
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out playResY);
                }
                else if (key.Equals("PlayResX", StringComparison.OrdinalIgnoreCase))
                {
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out playResX);
                }
                else if (key.Equals("WrapStyle", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out wrapStyle);
                }
                else if (key.Equals("ScriptType", StringComparison.OrdinalIgnoreCase))
                {
                    isSsa |= value.Equals("v4.00", StringComparison.OrdinalIgnoreCase);
                }

                continue;
            }

            if (key.Equals("Format", StringComparison.OrdinalIgnoreCase))
            {
                formats[section] = value.Split(',').Select(f => f.Trim().ToLowerInvariant()).ToArray();
                continue;
            }

            if (key.Equals("Style", StringComparison.OrdinalIgnoreCase) && formats.TryGetValue(section, out var styleFormat))
            {
                var fields = Fields(value, styleFormat);
                var style = new Style(
                    Get(fields, "name"),
                    ToAlignment(Get(fields, "alignment"), isSsa, 2),
                    Flag(Get(fields, "italic")),
                    Flag(Get(fields, "bold")),
                    Flag(Get(fields, "underline")),
                    ParseColor(Get(fields, "primarycolour")) ?? White);
                styles[style.Name] = style;
                continue;
            }

            if (key.Equals("Dialogue", StringComparison.OrdinalIgnoreCase) && formats.TryGetValue(section, out var eventFormat))
            {
                var fields = Fields(value, eventFormat);
                if (playResY <= 0)
                {
                    playResY = playResX > 0 ? playResX * 3 / 4 : 288;
                }

                var parsed = ParseEvent(fields, styles, isSsa, playResY, wrapStyle, events.Count);
                if (parsed is not null)
                {
                    events.Add(parsed);
                }
            }
        }

        return events;
    }

    private static SubtitleEvent? ParseEvent(Dictionary<string, string> fields, Dictionary<string, Style> styles, bool isSsa, double playResY, int wrapStyle, int order)
    {
        var start = ParseTime(Get(fields, "start"));
        var end = ParseTime(Get(fields, "end"));
        if (start is null || end is null || end <= start)
        {
            return null;
        }

        var styleName = Get(fields, "style").TrimStart('*');
        if (!styles.TryGetValue(styleName, out var style))
        {
            style = styles.TryGetValue("Default", out var fallback) ? fallback : new Style(styleName, 2, false, false, false, White);
        }

        var ev = new SubtitleEvent { Start = start.Value, End = end.Value, Order = order };
        var italic = style.Italic;
        var bold = style.Bold;
        var underline = style.Underline;
        var color = style.Color;
        var alignment = style.Alignment;
        double? posY = null;
        var karaoke = false;
        var drawing = false;

        var line = new List<TextRun>();
        ev.Lines.Add(line);
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length > 0 && !drawing)
            {
                line.Add(new TextRun(buffer.ToString(), italic, bold, underline, color));
            }

            buffer.Clear();
        }

        var text = Get(fields, "text");
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{')
            {
                var close = text.IndexOf('}', i + 1);
                if (close < 0)
                {
                    break;
                }

                Flush();
                foreach (var tag in SplitTags(text[(i + 1)..close]))
                {
                    ApplyTag(tag, style, styles, isSsa, ref italic, ref bold, ref underline, ref color, ref alignment, ref posY, ref karaoke, ref drawing);
                }

                i = close;
                continue;
            }

            if (c == '\\' && i + 1 < text.Length && text[i + 1] is 'N' or 'n' or 'h')
            {
                var code = text[i + 1];
                i++;
                if (code == 'h')
                {
                    buffer.Append(' ');
                }
                else if (code == 'N' || wrapStyle == 2)
                {
                    Flush();
                    line = new List<TextRun>();
                    ev.Lines.Add(line);
                }
                else
                {
                    buffer.Append(' ');
                }

                continue;
            }

            buffer.Append(c);
        }

        Flush();

        // Trim each line and drop empty ones (drawings, effects without text).
        for (var l = ev.Lines.Count - 1; l >= 0; l--)
        {
            var runs = ev.Lines[l];
            if (runs.Count > 0)
            {
                runs[0] = runs[0] with { Text = runs[0].Text.TrimStart() };
                runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd() };
            }

            runs.RemoveAll(r => r.Text.Length == 0);
            if (runs.Count == 0)
            {
                ev.Lines.RemoveAt(l);
            }
        }

        if (ev.Lines.Count == 0)
        {
            return null;
        }

        var names = string.Join(' ', style.Name, Get(fields, "name"), Get(fields, "effect"));
        ev.Kind = karaoke || SongName().IsMatch(names) ? EventKind.Song
            : posY is not null || SignName().IsMatch(names) ? EventKind.Sign
            : EventKind.Dialogue;

        ev.Region = posY is { } y
            ? y < playResY / 3 ? ScreenRegion.Top : y > playResY * 2 / 3 ? ScreenRegion.Bottom : ScreenRegion.Middle
            : alignment >= 7 ? ScreenRegion.Top : alignment >= 4 ? ScreenRegion.Middle : ScreenRegion.Bottom;

        return ev;
    }

    private static void ApplyTag(
        string tag,
        Style style,
        Dictionary<string, Style> styles,
        bool isSsa,
        ref bool italic,
        ref bool bold,
        ref bool underline,
        ref string color,
        ref int alignment,
        ref double? posY,
        ref bool karaoke,
        ref bool drawing)
    {
        if (tag.StartsWith("pos(", StringComparison.Ordinal) || tag.StartsWith("move(", StringComparison.Ordinal))
        {
            var match = Coordinates().Match(tag);
            if (match.Success)
            {
                posY = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            }
        }
        else if (tag.StartsWith("an", StringComparison.Ordinal) && int.TryParse(tag[2..], out var an) && an is >= 1 and <= 9)
        {
            alignment = an;
        }
        else if (tag.Length > 1 && tag[0] == 'a' && char.IsDigit(tag[1]))
        {
            alignment = ToAlignment(tag[1..], true, alignment);
        }
        else if (tag[0] is 'k' or 'K' && (tag.Length == 1 || char.IsDigit(tag[1]) || tag[1] is 'f' or 'o'))
        {
            karaoke = true;
        }
        else if (tag[0] == 'p' && tag.Length > 1 && char.IsDigit(tag[1]))
        {
            drawing = tag[1..] != "0";
        }
        else if (tag[0] == 'i' && (tag.Length == 1 || char.IsDigit(tag[1])))
        {
            italic = tag.Length == 1 ? style.Italic : tag[1..] != "0";
        }
        else if (tag[0] == 'b' && (tag.Length == 1 || char.IsDigit(tag[1])))
        {
            bold = tag.Length == 1 ? style.Bold : tag[1..] != "0";
        }
        else if (tag[0] == 'u' && (tag.Length == 1 || char.IsDigit(tag[1])))
        {
            underline = tag.Length == 1 ? style.Underline : tag[1..] != "0";
        }
        else if (tag.StartsWith("1c", StringComparison.Ordinal) || (tag[0] == 'c' && (tag.Length == 1 || tag[1] == '&')))
        {
            var value = tag[0] == '1' ? tag[2..] : tag[1..];
            color = value.Length == 0 ? style.Color : ParseColor(value) ?? color;
        }
        else if (tag[0] == 'r')
        {
            var reset = tag.Length > 1 && styles.TryGetValue(tag[1..], out var named) ? named : style;
            italic = reset.Italic;
            bold = reset.Bold;
            underline = reset.Underline;
            color = reset.Color;
        }
    }

    // Splits "\i1\pos(10,20)\t(0,100,\fs20)" into tags, keeping parenthesised arguments together.
    // Text before the first backslash is a comment ("{TL note}") and is ignored.
    private static IEnumerable<string> SplitTags(string block)
    {
        var tags = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var inTag = false;
        foreach (var c in block)
        {
            if (c == '\\' && depth == 0)
            {
                if (inTag && current.ToString().Trim() is { Length: > 0 } tag)
                {
                    tags.Add(tag);
                }

                current.Clear();
                inTag = true;
                continue;
            }

            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
            current.Append(c);
        }

        if (inTag && current.ToString().Trim() is { Length: > 0 } last)
        {
            tags.Add(last);
        }

        return tags;
    }

    private static Dictionary<string, string> Fields(string value, string[] format)
    {
        // The last field (Text) may contain commas.
        var parts = value.Split(',', format.Length);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < parts.Length && i < format.Length; i++)
        {
            fields[format[i]] = format[i] == "text" ? parts[i] : parts[i].Trim();
        }

        return fields;
    }

    private static string Get(Dictionary<string, string> fields, string name)
        => fields.TryGetValue(name, out var value) ? value : string.Empty;

    private static bool Flag(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number != 0;

    // SSA used 1-3 bottom, 5-7 top, 9-11 middle; ASS uses the numpad layout.
    private static int ToAlignment(string value, bool legacy, int fallback)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a))
        {
            return fallback;
        }

        if (!legacy)
        {
            return a is >= 1 and <= 9 ? a : fallback;
        }

        return a switch
        {
            >= 1 and <= 3 => a,
            >= 5 and <= 7 => a + 2,
            >= 9 and <= 11 => a - 5,
            _ => fallback
        };
    }

    // "&H00BBGGRR" or "&HBBGGRR&" (ASS) or a decimal number (SSA) to "#RRGGBB".
    private static string? ParseColor(string value)
    {
        value = value.Trim().Trim('&');
        long number;
        if (value.StartsWith('H') || value.StartsWith('h'))
        {
            if (!long.TryParse(value[1..].Trim('&'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number))
            {
                return null;
            }
        }
        else if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return null;
        }

        var r = number & 0xFF;
        var g = (number >> 8) & 0xFF;
        var b = (number >> 16) & 0xFF;
        return string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
    }

    private static long? ParseTime(string value)
    {
        // H:MM:SS.cc
        var parts = value.Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
        {
            return null;
        }

        return (long)Math.Round(((h * 3600) + (m * 60) + s) * 1000);
    }

    private sealed record Style(string Name, int Alignment, bool Italic, bool Bold, bool Underline, string Color);
}
