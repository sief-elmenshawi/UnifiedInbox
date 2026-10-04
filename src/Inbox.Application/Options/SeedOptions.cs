using System.ComponentModel.DataAnnotations;

namespace Inbox.Application.Options;

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    [Required]
    public string WorkspaceName { get; init; } = string.Empty;

    [Required]
    [EmailAddress]
    public string AdminEmail { get; init; } = string.Empty;

    [Required]
    public string AdminDisplayName { get; init; } = string.Empty;

    [Required]
    [MinLength(12)]
    public string AdminPassword { get; init; } = string.Empty;
}