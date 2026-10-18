using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AssToSrt.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the ids of the libraries to process. Empty means every video library.
    /// </summary>
    public string[] LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets a value indicating whether new or updated items are converted automatically.
    /// </summary>
    public bool AutoConvertNewItems { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether forced ASS tracks are converted too (written as .forced.srt).
    /// </summary>
    public bool ConvertForcedTracks { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether "Signs / Songs" style tracks are skipped.
    /// </summary>
    public bool SkipSignsAndSongs { get; set; } = true;

    /// <summary>
    /// Gets or sets the languages to convert, comma separated (e.g. "fr, en"). Empty means every language.
    /// </summary>
    public string Languages { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the SRT is named after the original track ("FR Full [ASS]" becomes "FR Full [SRT]").
    /// </summary>
    public bool UseTrackTitle { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether italic is kept.
    /// </summary>
    public bool KeepItalic { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether bold is kept.
    /// </summary>
    public bool KeepBold { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether underline is kept.
    /// </summary>
    public bool KeepUnderline { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether text colors are kept.
    /// </summary>
    public bool KeepColors { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether positions (top of the screen...) are kept.
    /// </summary>
    public bool KeepPositions { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether each line of dialogue is put on a single line.
    /// </summary>
    public bool SingleLine { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether subtitles displayed at the same time in the same screen area are merged
    /// into one block, so that they never overlap.
    /// </summary>
    public bool PreventOverlap { get; set; } = true;

    /// <summary>
    /// Gets or sets what to do with the translations of on-screen text: "Top", "Original" or "Remove".
    /// </summary>
    public string SignsMode { get; set; } = "Top";

    /// <summary>
    /// Gets or sets a value indicating whether song lyrics (opening, ending, karaoke) are removed.
    /// </summary>
    public bool RemoveSongs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the ASS tracks that have an SRT equivalent are hidden from Jellyfin's menus.
    /// The video files are not modified.
    /// </summary>
    public bool HideConvertedAss { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether signs are written in <see cref="SignColor"/>.
    /// </summary>
    public bool ColorSigns { get; set; } = true;

    /// <summary>
    /// Gets or sets the color of signs (#RRGGBB).
    /// </summary>
    public string SignColor { get; set; } = "#FFE066";

    /// <summary>
    /// Gets or sets a value indicating whether song lyrics are surrounded by ♪.
    /// </summary>
    public bool MarkSongs { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of characters per line, 0 to keep the original lines.
    /// </summary>
    public int MaxLineLength { get; set; } = 42;

    /// <summary>
    /// Gets or sets the minimum display time in milliseconds, 0 to keep the original timing.
    /// </summary>
    public int MinDurationMs { get; set; } = 1000;

    /// <summary>
    /// Gets or sets a value indicating whether the speaker name is written before the dialogue.
    /// </summary>
    public bool ShowSpeakerNames { get; set; }

    /// <summary>
    /// Gets or sets the conversion mode: "All" converts every video (nightly task and new videos),
    /// "Upcoming" only converts the episodes that follow the one being watched.
    /// </summary>
    public string ConversionMode { get; set; } = "All";

    /// <summary>
    /// Gets or sets how many following episodes are prepared when an episode starts.
    /// </summary>
    public int UpcomingCount { get; set; } = 3;

    /// <summary>
    /// Gets or sets what conversions do while a video is playing: "Slow" (read the file gently), "Pause" or "Normal".
    /// </summary>
    public string PlaybackBehavior { get; set; } = "Slow";

    /// <summary>
    /// Gets or sets a value indicating whether the nightly task and the conversion of new videos only run in the time window.
    /// </summary>
    public bool UseTimeWindow { get; set; }

    /// <summary>
    /// Gets or sets the hour (0-23) the time window starts.
    /// </summary>
    public int WindowStartHour { get; set; } = 1;

    /// <summary>
    /// Gets or sets the hour (0-23) the time window ends.
    /// </summary>
    public int WindowEndHour { get; set; } = 7;

    /// <summary>
    /// Gets or sets a value indicating whether conversions are reported in Jellyfin's activity log.
    /// </summary>
    public bool LogActivity { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the SRT files created by the plugin are deleted with their video.
    /// </summary>
    public bool CleanupOnRemove { get; set; } = true;

    /// <summary>
    /// Gets or sets the end of the last full conversion (ISO 8601, UTC), shown in the settings page.
    /// </summary>
    public string LastRunUtc { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the libraries processed by the last full conversion.
    /// </summary>
    public string LastRunLibraries { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of videos checked by the last full conversion.
    /// </summary>
    public int LastRunChecked { get; set; }

    /// <summary>
    /// Gets or sets the number of videos that received SRT files during the last full conversion.
    /// </summary>
    public int LastRunConverted { get; set; }

    /// <summary>
    /// Gets or sets the number of SRT files created by the last full conversion.
    /// </summary>
    public int LastRunCreated { get; set; }

    /// <summary>
    /// Gets or sets the number of errors of the last full conversion.
    /// </summary>
    public int LastRunErrors { get; set; }
}
