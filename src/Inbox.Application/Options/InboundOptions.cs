using System.ComponentModel.DataAnnotations;

namespace Inbox.Application.Options;

/// <summary>
/// How the inbound outbox is aged out. These are retention decisions, not tuning knobs: they say how
/// long the system keeps its own evidence, and getting them wrong costs either disk or the ability to
/// answer "what did the provider send last Tuesday".
/// </summary>
public sealed class InboundOptions
{
    public const string SectionName = "Inbound";

    /// <summary>
    /// Days a delivery that worked is kept. Long enough to investigate a reported problem, short enough
    /// that the table stays a log rather than an archive.
    /// </summary>
    [Range(1, 3650)]
    public int ProcessedRetentionDays { get; init; } = 14;

    /// <summary>
    /// Days a delivery that never applied is kept - three months by default, because these are the rows
    /// somebody has to act on, and the action often comes long after the delivery. A failed row also stops
    /// being aged out the moment an operator retries it and it succeeds.
    /// </summary>
    [Range(1, 3650)]
    public int FailedRetentionDays { get; init; } = 90;

    /// <summary>
    /// Rows deleted per purge statement. Bounded so a first run against a large table cannot hold a long
    /// lock or a long transaction; the worker comes back on its next pass for the rest.
    /// </summary>
    [Range(1, 10000)]
    public int PurgeBatchSize { get; init; } = 500;
}
