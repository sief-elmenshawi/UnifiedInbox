using System.Net;
using FluentAssertions;
using Inbox.Application.Conversations;
using Inbox.Infrastructure.Persistence;
using Inbox.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inbox.IntegrationTests;

/// <summary>
/// Retrying a failed send. A retry is finishing an old attempt rather than sending something new:
/// the same guards run again, the message keeps its sender and its place on the thread, and only
/// the delivery status moves. These tests prove both halves - the resend that works, and the
/// refusals that keep a retry from becoming a way around a closed window or a fresh decision.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class RetryMessageTests
{
    private readonly ApiFactory factory;

    public RetryMessageTests(ApiFactory factory)
    {
        this.factory = factory;

        // The stub graph host lives for the whole run, so a queued refusal and every recorded call
        // are cleared: a retry meets a provider that works unless this test broke it on purpose.
        StubGraph.Handler.Reset();
    }

    [Fact]
    public async Task A_failed_media_message_is_resent_and_becomes_sent()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        // The first attempt is refused at the provider's upload leg, which is the first call a
        // media send makes: the message is stored Failed with its file - exactly the state a retry
        // exists for.
        StubGraph.Handler.FailNext();

        var failed = await SendAsync(admin, conversation.Id, "your itinerary");

        failed.DeliveryStatus.Should().Be("Failed");
        failed.FailureReason.Should().Contain("24 hours");

        channel.Uploads.Should().BeEmpty();

        var retry = await admin.PostAsync($"/api/messages/{failed.Id}/retry");

        retry.StatusCode.Should().Be(HttpStatusCode.OK);

        var resent = await admin.ReadAsync<ConversationMessageResponse>(retry);

        resent.DeliveryStatus.Should().Be("Sent");
        resent.FailureReason.Should().BeNull();
        resent.ExternalMessageId.Should().StartWith("wamid.");
        resent.Text.Should().Be("your itinerary");

        // The sender of record is whoever wrote the message, not whoever clicked retry.
        resent.SenderAgentId.Should().Be(failed.SenderAgentId);

        // The retry walks the provider's two-call sequence again: upload, then a send whose
        // payload references the new upload's id, with the caption the agent wrote.
        var upload = channel.Uploads.Should().ContainSingle().Subject;
        upload.Content.Should().Equal(StubGraphHandler.DefaultImage);

        channel.LastSend!.Type.Should().Be("image");
        channel.LastSend.ReferencedMediaId.Should().Be(upload.Id);
        channel.LastSend.Payload.GetProperty("image").GetProperty("caption").GetString()
            .Should().Be("your itinerary");
    }

    [Fact]
    public async Task A_message_that_already_succeeded_is_not_retryable()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        var sent = await SendAsync(admin, conversation.Id);

        sent.DeliveryStatus.Should().Be("Sent");

        var retry = await admin.PostAsync($"/api/messages/{sent.Id}/retry");

        retry.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(retry)).ErrorCode.Should().Be("message.not_retryable");

        // Nothing was resent: the single upload and send on record are the original attempt's.
        channel.Uploads.Should().ContainSingle();
        channel.Sends.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_failed_text_message_is_not_retryable()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        // A text send is a single provider call, so the queued refusal is what it meets.
        StubGraph.Handler.FailNext();

        var sent = await admin.ReadAsync<ConversationMessageResponse>(
            await admin.PostAsync(
                $"/api/conversations/{conversation.Id}/messages",
                new SendMessageRequest("hello there")));

        sent.DeliveryStatus.Should().Be("Failed");

        var retry = await admin.PostAsync($"/api/messages/{sent.Id}/retry");

        // Resending text is a new reply, which the ordinary send route is for - with its own
        // guards about ownership and windows, which a retry must not skip.
        retry.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(retry)).ErrorCode.Should().Be("message.not_retryable");

        channel.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Retrying_outside_the_reply_window_is_refused()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        StubGraph.Handler.FailNext();

        var failed = await SendAsync(admin, conversation.Id);

        failed.DeliveryStatus.Should().Be("Failed");

        // A window may only ever move forward - RecordInbound refuses to shorten one that is open -
        // and this suite cannot wait 24 hours, so the honest way to a closed window is to move the
        // row's expiry: the passage of time in miniature. The retry must then be refused exactly
        // as a fresh send would be, because a retry is a send and not a way around the window.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.Conversations.SingleAsync(x => x.Id == conversation.Id);
        row.WindowExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();

        var retry = await admin.PostAsync($"/api/messages/{failed.Id}/retry");

        retry.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(retry)).ErrorCode.Should().Be("conversation.window_closed");

        channel.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_message_is_not_found()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;

        var retry = await admin.PostAsync($"/api/messages/{Guid.NewGuid()}/retry");

        retry.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ApiClient.ReadProblemAsync(retry)).ErrorCode.Should().Be("message.not_found");
    }

    private async Task<ConversationMessageResponse> SendAsync(
        ApiClient client,
        Guid conversationId,
        string? caption = null)
    {
        var response = await client.PostMultipartAsync(
            $"/api/conversations/{conversationId}/messages",
            "itinerary.png",
            StubGraphHandler.DefaultImage,
            "image/png",
            caption);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return await client.ReadAsync<ConversationMessageResponse>(response);
    }
}
