using Inbox.Application.Channels;
using Inbox.Application.Common;
using Inbox.Domain.Common;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inbox.Infrastructure.Channels;

public static class ChannelErrors
{
    public const string NotFound = "channel.not_found";
    public const string DuplicateExternalAccountId = "channel.duplicate_external_account_id";
    public const string Changed = "channel.changed";
}

/// <summary>
/// Channel administration. Credentials are protected here and only here: the plaintext is never
/// stored, never logged, and never leaves this class - the API can only report whether a secret is
/// present, which is all an admin screen needs to show "configured".
/// </summary>
public sealed class ChannelsService(
    AppDbContext db,
    IWorkspaceContext workspaceContext,
    IDataProtector protector,
    TimeProvider clock,
    ILogger<ChannelsService> logger) : IChannelsService
{
    /// <summary>
    /// Purpose string: the key ring separates channel credentials from anything else protected in
    /// this app, so rotating or purging one purpose never touches the other.
    /// </summary>
    public const string ProtectorPurpose = "Inbox.ChannelCredentials.v1";

    public async Task<Result<IReadOnlyList<ChannelResponse>>> ListAsync(CancellationToken cancellationToken = default)
    {
        var channels = await db.Channels
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<ChannelResponse>>(channels.Select(ToResponse).ToList());
    }

    public async Task<Result<ChannelResponse>> GetByIdAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        var channel = await FindAsync(channelId, tracked: false, cancellationToken);

        return channel is null
            ? Result.Failure<ChannelResponse>(Error.NotFound(ChannelErrors.NotFound, "Channel not found."))
            : Result.Success(ToResponse(channel));
    }

    public async Task<Result<ChannelResponse>> CreateAsync(
        CreateChannelRequest request,
        CancellationToken cancellationToken = default)
    {
        ChannelTypeParser.TryParse(request.Type, out var type);

        if (type == ChannelType.Test)
        {
            return Result.Failure<ChannelResponse>(Error.Validation(
                "channel.invalid_type", "Type must be WhatsApp, Instagram or Facebook."));
        }

        var workspaceId = await workspaceContext.GetWorkspaceIdAsync(cancellationToken);
        var externalAccountId = request.ExternalAccountId.Trim();

        // Unique per workspace: a webhook payload resolves a channel by this id, so two channels
        // answering to the same account would make routing ambiguous.
        if (await db.Channels.AnyAsync(x => x.ExternalAccountId == externalAccountId, cancellationToken))
        {
            return Result.Failure<ChannelResponse>(Error.Conflict(
                ChannelErrors.DuplicateExternalAccountId,
                "Another channel already uses this external account id."));
        }

        var channel = new Channel
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Type = type,
            DisplayName = request.DisplayName.Trim(),
            ExternalAccountId = externalAccountId,
            EncryptedAccessToken = Protect(request.AccessToken),
            EncryptedAppSecret = Protect(request.AppSecret),
            // Not encrypted: this value is echoed back to Meta in the webhook GET challenge, so it
            // is a shared check value rather than a credential that can send.
            WebhookVerifyToken = request.WebhookVerifyToken?.Trim() ?? string.Empty,
            IsActive = true,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        };

        db.Channels.Add(channel);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Two admins saving the same account id at the same time: the unique index is the
            // authority, so translate it instead of returning a 500. Any other database failure is
            // a real fault and must not be dressed up as a conflict.
            return Result.Failure<ChannelResponse>(Error.Conflict(
                ChannelErrors.DuplicateExternalAccountId,
                "Another channel already uses this external account id."));
        }

        // Identifiers only - the tokens must never reach the log, not even truncated.
        logger.LogInformation(
            "Channel {ChannelId} ({Type}) connected as {DisplayName}.",
            channel.Id,
            type,
            channel.DisplayName);

        return Result.Success(ToResponse(channel));
    }

    public async Task<Result<ChannelResponse>> UpdateAsync(
        Guid channelId,
        UpdateChannelRequest request,
        CancellationToken cancellationToken = default)
    {
        var channel = await FindAsync(channelId, tracked: true, cancellationToken);

        if (channel is null)
        {
            return Result.Failure<ChannelResponse>(Error.NotFound(ChannelErrors.NotFound, "Channel not found."));
        }

        if (!string.IsNullOrWhiteSpace(request.ExternalAccountId) && request.ExternalAccountId != channel.ExternalAccountId)
        {
            var externalAccountId = request.ExternalAccountId.Trim();

            if (await db.Channels.AnyAsync(
                    x => x.Id != channelId && x.ExternalAccountId == externalAccountId,
                    cancellationToken))
            {
                return Result.Failure<ChannelResponse>(Error.Conflict(
                    ChannelErrors.DuplicateExternalAccountId,
                    "Another channel already uses this external account id."));
            }

            channel.ExternalAccountId = externalAccountId;
        }

        channel.DisplayName = request.DisplayName.Trim();

        // Omitted means "unchanged", so renaming a channel never wipes the credentials an admin
        // cannot read back and would otherwise have to paste again.
        if (!string.IsNullOrEmpty(request.AccessToken))
        {
            channel.EncryptedAccessToken = Protect(request.AccessToken);
        }

        if (!string.IsNullOrEmpty(request.AppSecret))
        {
            channel.EncryptedAppSecret = Protect(request.AppSecret);
        }

        if (!string.IsNullOrWhiteSpace(request.WebhookVerifyToken))
        {
            channel.WebhookVerifyToken = request.WebhookVerifyToken.Trim();
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ChannelResponse>(Error.Conflict(
                ChannelErrors.Changed, "The channel was changed by someone else; reload and try again."));
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return Result.Failure<ChannelResponse>(Error.Conflict(
                ChannelErrors.DuplicateExternalAccountId,
                "Another channel already uses this external account id."));
        }

        return Result.Success(ToResponse(channel));
    }

    public async Task<Result> DeactivateAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        var channel = await FindAsync(channelId, tracked: true, cancellationToken);

        if (channel is null)
        {
            return Result.Failure(Error.NotFound(ChannelErrors.NotFound, "Channel not found."));
        }

        if (!channel.IsActive)
        {
            // Idempotent: two admins clicking "disconnect" should not race into a conflict.
            return Result.Success();
        }

        // Soft delete on purpose. Conversations and contacts point at this row, and a webhook may
        // still arrive in flight; keeping the row is what makes the history readable and lets the
        // inbound processor reject new traffic explicitly instead of failing to find the channel.
        channel.IsActive = false;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Channel {ChannelId} deactivated.", channel.Id);

        return Result.Success();
    }

    private Task<Channel?> FindAsync(Guid channelId, bool tracked, CancellationToken cancellationToken)
    {
        var query = tracked ? db.Channels : db.Channels.AsNoTracking();

        return query.FirstOrDefaultAsync(x => x.Id == channelId, cancellationToken);
    }

    /// <summary>An absent secret is stored as an empty string, never as null.</summary>
    private string Protect(string? secret) =>
        string.IsNullOrEmpty(secret) ? string.Empty : protector.Protect(secret);

    /// <summary>SQL Server unique index (2601) and unique constraint (2627) violations.</summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException sql && sql.Number is 2601 or 2627;

    private static ChannelResponse ToResponse(Channel channel) => new(
        channel.Id,
        ChannelTypeParser.ToApiValue(channel.Type),
        channel.DisplayName,
        channel.ExternalAccountId,
        channel.IsActive,
        HasSecret(channel.EncryptedAccessToken),
        HasSecret(channel.EncryptedAppSecret),
        HasSecret(channel.WebhookVerifyToken),
        channel.CreatedAtUtc);

    private static bool HasSecret(string? stored) => !string.IsNullOrEmpty(stored);
}