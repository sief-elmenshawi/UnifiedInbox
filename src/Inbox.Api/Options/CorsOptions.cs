using System.ComponentModel.DataAnnotations;

namespace Inbox.Api.Options;

public sealed class CorsOptions
{
    public const string SectionName = "Cors";
    public const string PolicyName = "InboxClient";

    /// <summary>Explicit origins only (the Angular dev server). Never "*" together with credentials.</summary>
    public string[] AllowedOrigins { get; init; } = [];
}