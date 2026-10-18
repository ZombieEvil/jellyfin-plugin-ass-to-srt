using System.Collections.Generic;

namespace Jellyfin.Plugin.AssToSrt.Services;

/// <summary>
/// Statistics shown in the settings page.
/// </summary>
public sealed class ConversionStats
{
    /// <summary>Gets or sets the number of SRT files created by the plugin.</summary>
    public int SrtFiles { get; set; }

    /// <summary>Gets or sets the number of videos that have SRT files created by the plugin.</summary>
    public int Videos { get; set; }

    /// <summary>Gets or sets the size of the SRT files in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets the number of videos whose ASS tracks are hidden.</summary>
    public int HiddenVideos { get; set; }

    /// <summary>Gets or sets the number of videos waiting to be converted.</summary>
    public int Queued { get; set; }
}

/// <summary>
/// One ASS track of the preview.
/// </summary>
public sealed class PreviewTrack
{
    /// <summary>Gets or sets the track name.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the track is forced.</summary>
    public bool Forced { get; set; }

    /// <summary>Gets or sets the original ASS events.</summary>
    public List<PreviewLine> Original { get; set; } = new();

    /// <summary>Gets or sets the converted subtitles.</summary>
    public List<PreviewCue> Converted { get; set; } = new();
}

/// <summary>
/// An original ASS event.
/// </summary>
/// <param name="Start">Start in milliseconds.</param>
/// <param name="End">End in milliseconds.</param>
/// <param name="Kind">Dialogue, Sign or Song.</param>
/// <param name="Row">Top, Middle or Bottom.</param>
/// <param name="Column">Left, Center or Right.</param>
/// <param name="Text">Plain text.</param>
public sealed record PreviewLine(long Start, long End, string Kind, string Row, string Column, string Text);

/// <summary>
/// A converted SRT subtitle.
/// </summary>
/// <param name="Start">Start in milliseconds.</param>
/// <param name="End">End in milliseconds.</param>
/// <param name="Row">Top, Middle or Bottom.</param>
/// <param name="Column">Left, Center or Right.</param>
/// <param name="Lines">Formatted lines (SRT tags).</param>
public sealed record PreviewCue(long Start, long End, string Row, string Column, List<string> Lines);
