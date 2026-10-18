using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.AssToSrt.Services;

/// <summary>
/// What to do with the translations of on-screen text.
/// </summary>
public enum SignsMode
{
    /// <summary>Show them at the top of the screen, away from the dialogue.</summary>
    Top,

    /// <summary>Keep the screen area they had in the ASS file.</summary>
    Original,

    /// <summary>Do not include them.</summary>
    Remove
}

/// <summary>
/// SRT output options.
/// </summary>
public sealed class SrtOptions
{
    /// <summary>Gets or sets a value indicating whether italic is kept.</summary>
    public bool KeepItalic { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether bold is kept.</summary>
    public bool KeepBold { get; set; }

    /// <summary>Gets or sets a value indicating whether underline is kept.</summary>
    public bool KeepUnderline { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether colors other than white are kept.</summary>
    public bool KeepColors { get; set; }

    /// <summary>Gets or sets a value indicating whether subtitles keep their screen area (top, middle, bottom).</summary>
    public bool KeepPositions { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the lines of a subtitle are joined.</summary>
    public bool SingleLine { get; set; }

    /// <summary>Gets or sets a value indicating whether simultaneous subtitles of a screen area are merged into one block.</summary>
    public bool PreventOverlap { get; set; } = true;

    /// <summary>Gets or sets what to do with signs.</summary>
    public SignsMode Signs { get; set; } = SignsMode.Top;

    /// <summary>Gets or sets a value indicating whether song lyrics are removed.</summary>
    public bool RemoveSongs { get; set; }
}

/// <summary>
/// Turns ASS events into an SRT document that simple players display without overlapping text.
/// </summary>
public static class SrtWriter
{
    // Boundaries closer than this are merged, so that two lines changing almost together do not flash.
    private const long SnapMilliseconds = 80;

    /// <summary>
    /// Writes the SRT document.
    /// </summary>
    /// <param name="events">ASS events.</param>
    /// <param name="options">Options.</param>
    /// <returns>The SRT content, empty when nothing is left to display.</returns>
    public static string Write(IEnumerable<SubtitleEvent> events, SrtOptions options)
    {
        var items = new List<Item>();
        var seen = new HashSet<(long, long, ScreenRegion, string)>();

        foreach (var ev in events.OrderBy(e => e.Start).ThenBy(e => e.Order))
        {
            if ((ev.Kind == EventKind.Song && options.RemoveSongs) || (ev.Kind == EventKind.Sign && options.Signs == SignsMode.Remove))
            {
                continue;
            }

            var region = !options.KeepPositions ? ScreenRegion.Bottom
                : ev.Kind == EventKind.Sign && options.Signs == SignsMode.Top ? ScreenRegion.Top
                : ev.Region;

            var lines = Render(ev, options);

            // Signs are often drawn twice (border layer + text layer): keep a single copy.
            if (lines.Count == 0 || !seen.Add((ev.Start, ev.End, region, string.Join("\n", lines))))
            {
                continue;
            }

            items.Add(new Item(ev.Start, ev.End, ev.Order, region, lines));
        }

        if (items.Count == 0)
        {
            return string.Empty;
        }

        var cues = options.PreventOverlap ? Merge(items) : items.Select(i => new Cue(i.Start, i.End, i.Region, i.Lines)).ToList();

        var output = new StringBuilder();
        var number = 0;
        foreach (var cue in cues.OrderBy(c => c.Start).ThenByDescending(c => c.Region))
        {
            number++;
            output.Append(number.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            output.Append(Time(cue.Start)).Append(" --> ").Append(Time(cue.End)).Append("\r\n");
            var prefix = cue.Region switch
            {
                ScreenRegion.Top => "{\\an8}",
                ScreenRegion.Middle => "{\\an5}",
                _ => string.Empty
            };
            for (var i = 0; i < cue.Lines.Count; i++)
            {
                output.Append(i == 0 ? prefix : string.Empty).Append(cue.Lines[i]).Append("\r\n");
            }

            output.Append("\r\n");
        }

        return output.ToString();
    }

    // At any time, a screen area shows a single cue holding every active line, in order of appearance.
    private static List<Cue> Merge(List<Item> items)
    {
        var snapped = Snap(items);
        var cues = new List<Cue>();

        foreach (var group in snapped.GroupBy(i => i.Region))
        {
            var list = group.ToList();
            var bounds = list.SelectMany(i => new[] { i.Start, i.End }).Distinct().OrderBy(t => t).ToList();
            Cue? previous = null;

            for (var b = 0; b < bounds.Count - 1; b++)
            {
                var from = bounds[b];
                var to = bounds[b + 1];
                var lines = list
                    .Where(i => i.Start < to && i.End > from)
                    .OrderBy(i => i.Start)
                    .ThenBy(i => i.Order)
                    .SelectMany(i => i.Lines)
                    .Distinct()
                    .ToList();

                if (lines.Count == 0)
                {
                    previous = null;
                    continue;
                }

                if (previous is not null && previous.End == from && previous.Lines.SequenceEqual(lines))
                {
                    previous.End = to;
                    continue;
                }

                previous = new Cue(from, to, group.Key, lines);
                cues.Add(previous);
            }
        }

        return cues;
    }

    private static List<Item> Snap(List<Item> items)
    {
        var times = items.SelectMany(i => new[] { i.Start, i.End }).Distinct().OrderBy(t => t).ToList();
        var map = new Dictionary<long, long>();
        var anchor = times[0];
        foreach (var t in times)
        {
            if (t - anchor > SnapMilliseconds)
            {
                anchor = t;
            }

            map[t] = anchor;
        }

        return items
            .Select(i => i with { Start = map[i.Start], End = map[i.End] })
            .Where(i => i.End > i.Start)
            .ToList();
    }

    private static List<string> Render(SubtitleEvent ev, SrtOptions options)
    {
        var lines = ev.Lines
            .Select(runs => string.Concat(Coalesce(runs, options).Select(r => Format(r, options))).Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (!options.SingleLine || lines.Count < 2)
        {
            return lines;
        }

        // Dialogue lines starting with a dash stay on their own line.
        var joined = new List<string> { lines[0] };
        foreach (var line in lines.Skip(1))
        {
            if (StartsWithDash(line))
            {
                joined.Add(line);
            }
            else
            {
                joined[^1] += " " + line;
            }
        }

        return joined;
    }

    // Adjacent runs that end up with the same visible formatting are written as one.
    private static List<TextRun> Coalesce(List<TextRun> runs, SrtOptions options)
    {
        var result = new List<TextRun>();
        foreach (var run in runs.Select(r => Effective(r, options)))
        {
            if (result.Count > 0 && result[^1] with { Text = string.Empty } == run with { Text = string.Empty })
            {
                result[^1] = result[^1] with { Text = result[^1].Text + run.Text };
            }
            else
            {
                result.Add(run);
            }
        }

        return result;
    }

    private static TextRun Effective(TextRun run, SrtOptions options) => new(
        run.Text,
        run.Italic && options.KeepItalic,
        run.Bold && options.KeepBold,
        run.Underline && options.KeepUnderline,
        options.KeepColors && !run.Color.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase) ? run.Color : string.Empty);

    private static string Format(TextRun run, SrtOptions options)
    {
        var text = run.Text;
        if (run.Underline)
        {
            text = "<u>" + text + "</u>";
        }

        if (run.Italic)
        {
            text = "<i>" + text + "</i>";
        }

        if (run.Bold)
        {
            text = "<b>" + text + "</b>";
        }

        if (run.Color.Length > 0)
        {
            text = "<font color=\"" + run.Color + "\">" + text + "</font>";
        }

        return text;
    }

    private static bool StartsWithDash(string line)
    {
        var plain = line.TrimStart();
        while (plain.StartsWith('<'))
        {
            var close = plain.IndexOf('>', StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            plain = plain[(close + 1)..].TrimStart();
        }

        return plain.Length > 0 && plain[0] is '-' or '–' or '—';
    }

    private static string Time(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");
    }

    private sealed record Item(long Start, long End, int Order, ScreenRegion Region, List<string> Lines);

    private sealed class Cue
    {
        public Cue(long start, long end, ScreenRegion region, List<string> lines)
        {
            Start = start;
            End = end;
            Region = region;
            Lines = lines;
        }

        public long Start { get; }

        public long End { get; set; }

        public ScreenRegion Region { get; }

        public List<string> Lines { get; }
    }
}
