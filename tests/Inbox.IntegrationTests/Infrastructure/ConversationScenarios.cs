using System.Net;
using FluentAssertions;
using Inbox.Api.Controllers;
using Inbox.Application.Common;
using Inbox.Application.Conversations;
using Xunit.Sdk;

namespace Inbox.IntegrationTests.Infrastructure;

/// <summary>
/// Drives the dev simulator and waits for the background worker, so the tests assert on what an
/// agent would actually see a moment later rather than on the queue.
/// </summary>
internal static class ConversationScenarios
{
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromSeconds(20);

    /// <summary>A fresh contact id per call, so the shared database keeps the tests independent.</summary>
    public static string NewExternalContactId() => $"wa-{Guid.NewGuid():N}";

    /// <summary>
    /// The tests find their conversation again by display name, so a hardcoded name shared with
    /// another test would make them read each other's thread. The suffix keeps them independent.
    /// </summary>
    public static string UniqueName(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    /// <summary>
    /// Posts one inbound message and returns the conversation it landed in. Waits until the stored
    /// message exists, which is the point the processor has finished with the conversation.
    /// </summary>
    public static async Task<(string ExternalContactId, ConversationDetailResponse Conversation)> DeliverAsync(
        ApiClient admin,
        string? displayName = null,
        string text = "Hello, is anyone there?",
        string? externalMessageId = null,
        string? externalContactId = null)
    {
        var contactId = externalContactId ?? NewExternalContactId();

        // The endpoint generates one when the caller does not supply it, and the test needs to know
        // which id to look for afterwards.
        var messageId = externalMessageId ?? $"sim-{Guid.NewGuid():N}";

        var response = await admin.PostAsync(
            "/api/dev/simulate-inbound",
            new SimulateInboundRequest(contactId, displayName, messageId, text, null));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // The processor falls back to the external id when no name is supplied.
        var conversation = await WaitForMessageAsync(admin, displayName ?? contactId, messageId);

        return (contactId, conversation);
    }

    /// <summary>
    /// The endpoint only queues, so the assertion has to wait for the worker: find the conversation
    /// the contact belongs to, then wait for the exact message to show up in its history.
    /// </summary>
    public static async Task<ConversationDetailResponse> WaitForMessageAsync(
        ApiClient client,
        string contactDisplayName,
        string externalMessageId)
    {
        var deadline = DateTimeOffset.UtcNow + ProcessingTimeout;
        var conversationId = Guid.Empty;

        while (true)
        {
            if (conversationId != Guid.Empty)
            {
                var messages = await MessagesAsync(client, conversationId);

                if (messages.Items.Any(x => x.ExternalMessageId == externalMessageId))
                {
                    return await OpenAsync(client, conversationId);
                }
            }
            else
            {
                var page = await ListAsync(client, $"filter=all&search={Uri.EscapeDataString(contactDisplayName)}");
                var match = page.Items.FirstOrDefault(x => x.ContactDisplayName == contactDisplayName);

                conversationId = match?.Id ?? Guid.Empty;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new XunitException(
                    $"The inbound message {externalMessageId} was still not stored after " +
                    $"{ProcessingTimeout.TotalSeconds:0} seconds.");
            }

            await Task.Delay(100);
        }
    }

    public static async Task<ConversationDetailResponse> OpenAsync(ApiClient client, Guid conversationId) =>
        await client.ReadAsync<ConversationDetailResponse>(
            await client.Http.GetAsync($"/api/conversations/{conversationId}"));

    public static async Task<ConversationDetailResponse> ActionAsync(
        ApiClient client,
        Guid conversationId,
        string action,
        object? body = null)
    {
        var url = $"/api/conversations/{conversationId}/{action}";
        var response = body is null
            ? await client.PostAsync(url)
            : await client.PostAsync(url, body);

        return await client.ReadAsync<ConversationDetailResponse>(response);
    }

    public static async Task<PagedResult<ConversationListItemResponse>> ListAsync(
        ApiClient client,
        string query = "filter=all&pageSize=50") =>
        await client.ReadAsync<PagedResult<ConversationListItemResponse>>(
            await client.Http.GetAsync($"/api/conversations?{query}"));

    public static async Task<PagedResult<ConversationMessageResponse>> MessagesAsync(
        ApiClient client,
        Guid conversationId,
        int pageSize = 50) =>
        await client.ReadAsync<PagedResult<ConversationMessageResponse>>(
            await client.Http.GetAsync($"/api/conversations/{conversationId}/messages?pageSize={pageSize}"));
}