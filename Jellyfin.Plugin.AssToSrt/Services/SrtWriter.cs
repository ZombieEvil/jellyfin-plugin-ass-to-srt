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

    /// <summary>Keep the screen area they had in the ASS file (9 positions).</summary>
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

    /// <summary>Gets or sets a value indicating whether subtitles keep their screen area.</summary>
    public bool KeepPositions { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the lines of a subtitle are joined.</summary>
    public bool SingleLine { get; set; }

    /// <summary>Gets or sets a value indicating whether simultaneous subtitles of a screen area are merged into one block.</summary>
    public bool PreventOverlap { get; set; } = true;

    /// <summary>Gets or sets what to do with signs.</summary>
    public SignsMode Signs { get; set; } = SignsMode.Top;

    /// <summary>Gets or sets the color of signs (#RRGGBB), empty for none.</summary>
    public string SignColor { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether song lyrics are removed.</summary>
    public bool RemoveSongs { get; set; }

    /// <summary>Gets or sets a value indicating whether song lyrics are surrounded by ♪.</summary>
    public bool MarkSongs { get; set; } = true;

    /// <summary>Gets or sets the maximum number of characters per line, 0 to keep the lines as they are.</summary>
    public int MaxLineLength { get; set; } = 42;

    /// <summary>Gets or sets the minimum display time in milliseconds, 0 to keep the original timing.</summary>
    public int MinDurationMs { get; set; } = 1000;

    /// <summary>Gets or sets a value indicating whether the speaker name is written before the dialogue.</summary>
    public bool ShowSpeakerNames { get; set; }
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
        => Format(BuildCues(events, options));

    /// <summary>
    /// Builds the cues, used by the preview.
    /// </summary>
    /// <param name="events">ASS events.</param>
    /// <param name="options">Options.</param>
    /// <returns>The cues in display order.</returns>
    public static List<SrtCue> BuildCues(IEnumerable<SubtitleEvent> events, SrtOptions options)
    {
        var items = new List<Item>();
        var seen = new HashSet<(long, long, ScreenRegion, string)>();

        foreach (var ev in events.OrderBy(e => e.Start).ThenBy(e => e.Order))
        {
            if ((ev.Kind == EventKind.Song && options.RemoveSongs) || (ev.Kind == EventKind.Sign && options.Signs == SignsMode.Remove))
            {
                continue;
            }

            var (row, column) = !options.KeepPositions ? (ScreenRegion.Bottom, ScreenColumn.Center)
                : ev.Kind == EventKind.Sign && options.Signs == SignsMode.Top ? (ScreenRegion.Top, ScreenColumn.Center)
                : (ev.Region, ev.Column);

            var lines = Render(ev, options);

            // Signs are often drawn twice (border layer + text layer): keep a single copy.
            if (lines.Count == 0 || !seen.Add((ev.Start, ev.End, row, string.Join("\n", lines))))
            {
                continue;
            }

            items.Add(new Item(ev.Start, ev.End, ev.Order, row, column, lines));
        }

        if (items.Count == 0)
        {
            return new List<SrtCue>();
        }

        items = ExtendShortItems(items, options.MinDurationMs);

        var cues = options.PreventOverlap
            ? Merge(items)
            : items.Select(i => new SrtCue(i.Start, i.End, i.Row, i.Column, i.Lines)).ToList();

        return cues.OrderBy(c => c.Start).ThenByDescending(c => c.Row).ToList();
    }

    private static string Format(List<SrtCue> cues)
    {
        var output = new StringBuilder();
        var number = 0;
        foreach (var cue in cues)
        {
            number++;
            output.Append(number.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            output.Append(Time(cue.Start)).Append(" --> ").Append(Time(cue.End)).Append("\r\n");
            var prefix = PositionTag(cue.Row, cue.Column);
            for (var i = 0; i < cue.Lines.Count; i++)
            {
                output.Append(i == 0 ? prefix : string.Empty).Append(cue.Lines[i]).Append("\r\n");
            }

            output.Append("\r\n");
        }

        return output.ToString();
    }

    // Numpad layout used by {\anN}: 1-3 bottom, 4-6 middle, 7-9 top; bottom center needs no tag.
    private static string PositionTag(ScreenRegion row, ScreenColumn column)
    {
        var an = (row switch { ScreenRegion.Top => 7, ScreenRegion.Middle => 4, _ => 1 }) + (int)column;
        return an == 2 ? string.Empty : "{\\an" + an.ToString(CultureInfo.InvariantCulture) + "}";
    }

    // Short subtitles stay readable, without running into the next subtitle of the same area.
    private static List<Item> ExtendShortItems(List<Item> items, int minDuration)
    {
        if (minDuration <= 0)
        {
            return items;
        }

        var result = new List<Item>(items.Count);
        foreach (var row in items.GroupBy(i => i.Row))
        {
            var list = row.OrderBy(i => i.Start).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (item.End - item.Start < minDuration)
                {
                    var next = list.Skip(i + 1).FirstOrDefault(n => n.Start > item.Start);
                    var limit = next?.Start ?? long.MaxValue;
                    item = item with { End = Math.Max(item.End, Math.Min(item.Start + minDuration, limit)) };
                }

                result.Add(item);
            }
        }

        return result;
    }

    // At any time, a row of the screen shows a single cue holding every active line, in order of appearance.
    private static List<SrtCue> Merge(List<Item> items)
    {
        var snapped = Snap(items);
        var cues = new List<SrtCue>();

        foreach (var group in snapped.GroupBy(i => i.Row))
        {
            var list = group.ToList();
            var bounds = list.SelectMany(i => new[] { i.Start, i.End }).Distinct().OrderBy(t => t).ToList();
            SrtCue? previous = null;

            for (var b = 0; b < bounds.Count - 1; b++)
            {
                var from = bounds[b];
                var to = bounds[b + 1];
                var active = list
                    .Where(i => i.Start < to && i.End > from)
                    .OrderBy(i => i.Start)
                    .ThenBy(i => i.Order)
                    .ToList();
                var lines = active.SelectMany(i => i.Lines).Distinct().ToList();

                if (lines.Count == 0)
                {
                    previous = null;
                    continue;
                }

                // Left or right only when every active subtitle of the row agrees, otherwise centered.
                var columns = active.Select(i => i.Column).Distinct().ToList();
                var column = columns.Count == 1 ? columns[0] : ScreenColumn.Center;

                if (previous is not null && previous.End == from && previous.Column == column && previous.Lines.SequenceEqual(lines))
                {
                    previous.End = to;
                    continue;
                }

                previous = new SrtCue(from, to, group.Key, column, lines);
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
        var lines = ev.Lines.Select(l => l.ToList()).Where(l => l.Count > 0).ToList();
        if (lines.Count == 0)
        {
            return new List<string>();
        }

        if (options.ShowSpeakerNames && ev.Kind == EventKind.Dialogue && ev.Speaker.Length > 0)
        {
            var first = lines[0][0];
            lines[0].Insert(0, first with { Text = ev.Speaker + " : ", Italic = false });
        }

        if (options.SingleLine && lines.Count > 1)
        {
            lines = JoinLines(lines);
        }

        if (options.MaxLineLength > 0)
        {
            lines = lines.SelectMany(l => Wrap(l, options.MaxLineLength)).ToList();
        }

        var rendered = lines
            .Select(runs => string.Concat(Coalesce(runs, options).Select(FormatRun)).Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (ev.Kind == EventKind.Song && options.MarkSongs)
        {
            rendered = rendered.Select(l => options.KeepItalic ? "<i>♪ " + l + " ♪</i>" : "♪ " + l + " ♪").ToList();
        }

        if (ev.Kind == EventKind.Sign && options.SignColor.Length > 0)
        {
            rendered = rendered.Select(l => "<font color=\"" + options.SignColor + "\">" + l + "</font>").ToList();
        }

        return rendered;
    }

    // Dialogue lines starting with a dash stay on their own line.
    private static List<List<TextRun>> JoinLines(List<List<TextRun>> lines)
    {
        var joined = new List<List<TextRun>> { lines[0].ToList() };
        foreach (var line in lines.Skip(1))
        {
            var text = string.Concat(line.Select(r => r.Text)).TrimStart();
            if (text.Length > 0 && text[0] is '-' or '–' or '—')
            {
                joined.Add(line.ToList());
            }
            else
            {
                var last = joined[^1];
                last.Add(line[0] with { Text = " " });
                last.AddRange(line);
            }
        }

        return joined;
    }

    // Splits a long line into balanced lines at spaces, keeping the formatting of each piece.
    private static IEnumerable<List<TextRun>> Wrap(List<TextRun> line, int max)
    {
        var plain = string.Concat(line.Select(r => r.Text));
        if (plain.Length <= max)
        {
            return new[] { line };
        }

        var count = (int)Math.Ceiling(plain.Length / (double)max);
        var breaks = new List<int>();
        var last = -1;
        for (var i = 1; i < count; i++)
        {
            var target = plain.Length * i / count;
            var best = -1;
            for (var d = 0; d < plain.Length; d++)
            {
                if (target - d > last && target - d >= 0 && plain[target - d] == ' ')
                {
                    best = target - d;
                    break;
                }

                if (target + d < plain.Length && plain[target + d] == ' ')
                {
                    best = target + d;
                    break;
                }
            }

            if (best <= last)
            {
                break;
            }

            breaks.Add(best);
            last = best;
        }

        if (breaks.Count == 0)
        {
            return new[] { line };
        }

        // Cut the runs at the break positions; the space at a break is dropped.
        var result = new List<List<TextRun>> { new() };
        var offset = 0;
        var next = 0;
        foreach (var run in line)
        {
            var start = 0;
            while (next < breaks.Count && breaks[next] < offset + run.Text.Length)
            {
                var cut = breaks[next] - offset;
                if (cut > start)
                {
                    result[^1].Add(run with { Text = run.Text[start..cut] });
                }

                result.Add(new List<TextRun>());
                start = cut + 1;
                next++;
            }

            if (start < run.Text.Length)
            {
                result[^1].Add(run with { Text = run.Text[start..] });
            }

            offset += run.Text.Length;
        }

        return result.Where(l => l.Count > 0);
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

    private static string FormatRun(TextRun run)
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

    private static string Time(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");
    }

    private sealed record Item(long Start, long End, int Order, ScreenRegion Row, ScreenColumn Column, List<string> Lines);
}

/// <summary>
/// A subtitle of the SRT output.
/// </summary>
public sealed class SrtCue
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SrtCue"/> class.
    /// </summary>
    /// <param name="start">Start in milliseconds.</param>
    /// <param name="end">End in milliseconds.</param>
    /// <param name="row">Screen row.</param>
    /// <param name="column">Screen column.</param>
    /// <param name="lines">Formatted lines.</param>
    public SrtCue(long start, long end, ScreenRegion row, ScreenColumn column, List<string> lines)
    {
        Start = start;
        End = end;
        Row = row;
        Column = column;
        Lines = lines;
    }

    /// <summary>Gets the start in milliseconds.</summary>
    public long Start { get; }

    /// <summary>Gets or sets the end in milliseconds.</summary>
    public long End { get; set; }

    /// <summary>Gets the screen row.</summary>
    public ScreenRegion Row { get; }

    /// <summary>Gets the screen column.</summary>
    public ScreenColumn Column { get; }

    /// <summary>Gets the formatted lines.</summary>
    public List<string> Lines { get; }
}
