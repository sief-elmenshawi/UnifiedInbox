using Inbox.Application.Channels;
using Inbox.Application.Common;
using Inbox.Domain.Common;

namespace Inbox.Application.Conversations;

public enum ConversationFilter
{
    /// <summary>Everything that is not resolved.</summary>
    All = 0,
    Unassigned = 1,
    Mine = 2,
    Resolved = 3
}

public sealed record ConversationContactSummary(Guid Id, string DisplayName, string ExternalId, string? AvatarUrl);

public sealed record ConversationChannelSummary(Guid Id, string Type, string DisplayName);

public sealed record ConversationListItemResponse(
    Guid Id,
    Guid ContactId,
    string ContactDisplayName,
    Guid ChannelId,
    string ChannelType,
    Guid? AssignedAgentId,
    string? AssignedAgentName,
    string Status,
    int UnreadCount,
    string? LastMessagePreview,
    DateTime LastMessageAtUtc,
    DateTime? LastInboundAtUtc,
    bool ReplyWindowOpen);

public sealed record ConversationDetailResponse(
    Guid Id,
    ConversationContactSummary Contact,
    ConversationChannelSummary Channel,
    Guid? AssignedAgentId,
    string? AssignedAgentName,
    string Status,
    int UnreadCount,
    string? LastMessagePreview,
    DateTime LastMessageAtUtc,
    DateTime? LastInboundAtUtc,
    DateTime? WindowExpiresAtUtc,
    bool ReplyWindowOpen,
    DateTime CreatedAtUtc,
    IReadOnlyList<ConversationEventResponse> Events);

public sealed record ConversationEventResponse(
    Guid Id,
    string Type,
    Guid? ActorAgentId,
    string? ActorAgentName,
    Guid? TargetAgentId,
    string? TargetAgentName,
    DateTime CreatedAtUtc);

/// <summary>
/// The message as the thread renders it. It carries its own conversation id because it is also the
/// payload of a live <c>MessageReceived</c> event, where the client has to know which thread it
/// belongs to.
/// </summary>
public sealed record ConversationMessageResponse(
    Guid Id,
    Guid ConversationId,
    string Direction,
    string SenderType,
    Guid? SenderAgentId,
    string? SenderAgentName,
    string Type,
    string? Text,
    string? ExternalMessageId,
    string DeliveryStatus,
    string? FailureReason,
    DateTime CreatedAtUtc,
    IReadOnlyList<MessageAttachmentResponse> Attachments);

/// <summary>
/// A file on a message, as the thread renders it.
/// </summary>
/// <remarks>
/// <para>
/// There is no path here, and nothing a client could turn into one: the file is fetched by id from
/// <c>GET /api/attachments/{id}</c>. That endpoint also refuses anything that is not
/// <see cref="AttachmentAvailability.Available"/>, so a stale page cannot download a file we
/// deliberately did not keep.
/// </para>
/// <para>
/// <paramref name="Availability"/> and <paramref name="UnavailableReason"/> are present on every
/// attachment, not only the broken ones, so the client can render "file not kept: too large" without a
/// second request.
/// </para>
/// </remarks>
public sealed record MessageAttachmentResponse(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Availability,
    string? UnavailableReason);

public sealed record SendMessageRequest(string Text);

/// <summary>
/// An approved template send. Allowed outside the 24-hour window, which is the only reason it
/// exists: <c>bodyParameters</c> are positional and map to the <c>{{1}}</c> placeholders in the
/// template body.
/// </summary>
public sealed record SendTemplateRequest(
    string Name,
    string LanguageCode,
    IReadOnlyList<string> BodyParameters,
    string? Category = null);

public sealed record TransferConversationRequest(Guid TargetAgentId);

public interface IConversationsService
{
    Task<Result<PagedResult<ConversationListItemResponse>>> ListAsync(
        Guid agentId,
        ConversationFilter filter,
        Guid? channelId = null,
        string? search = null,
        int pageSize = 25,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<Result<ConversationDetailResponse>> GetAsync(
        Guid agentId,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Newest first, with a <c>before</c> cursor pointing at a known message id.</summary>
    Task<Result<PagedResult<ConversationMessageResponse>>> GetMessagesAsync(
        Guid agentId,
        Guid conversationId,
        int pageSize = 50,
        Guid? before = null,
        CancellationToken cancellationToken = default);

    Task<Result<ConversationDetailResponse>> TakeAsync(
        Guid agentId,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<Result<ConversationDetailResponse>> ReleaseAsync(
        Guid agentId,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<Result<ConversationDetailResponse>> TransferAsync(
        Guid agentId,
        Guid conversationId,
        Guid targetAgentId,
        CancellationToken cancellationToken = default);

    Task<Result<ConversationDetailResponse>> ResolveAsync(
        Guid agentId,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<Result<ConversationDetailResponse>> ReopenAsync(
        Guid agentId,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<Result> MarkReadAsync(
        Guid agentId,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Sends through the channel adapter and stores the result, including failures.</summary>
    Task<Result<ConversationMessageResponse>> SendMessageAsync(
        Guid agentId,
        Guid conversationId,
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an approved template. Deliberately not blocked by the reply window: outside 24 hours a
    /// free-form message is refused by the provider, and the template is the only legal way to reach
    /// the customer.
    /// </summary>
    Task<Result<ConversationMessageResponse>> SendTemplateAsync(
        Guid agentId,
        Guid conversationId,
        TemplateMessage template,
        CancellationToken cancellationToken = default);
}