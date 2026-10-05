using System.ComponentModel.DataAnnotations;

namespace Inbox.Application.Options;

/// <summary>
/// Where the Meta Graph API lives. Both values are configuration because Meta versions its API and
/// the base URL moves between the graph host and regional ones; hardcoding either would make an
/// upgrade a code change.
/// </summary>
public sealed class MetaOptions
{
    public const string SectionName = "Meta";

    /// <summary>e.g. "v21.0". Required to send; the webhook side does not need it.</summary>
    [Required]
    public string GraphApiVersion { get; set; } = string.Empty;

    public string GraphBaseUrl { get; set; } = "https://graph.facebook.com";

    /// <summary>
    /// The version segment to put in a Graph URL. Accepts both "v21.0" and "21.0" because the Meta
    /// dashboard shows the version with the "v" and it is easy to paste either way.
    /// </summary>
    public string VersionSegment => GraphApiVersion.StartsWith('v') ? GraphApiVersion : $"v{GraphApiVersion}";
}