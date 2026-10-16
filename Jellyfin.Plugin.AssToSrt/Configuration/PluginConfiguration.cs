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
