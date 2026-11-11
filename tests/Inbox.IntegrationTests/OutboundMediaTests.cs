using System.Net;
using System.Text;
using FluentAssertions;
using Inbox.Application.Conversations;
using Inbox.IntegrationTests.Infrastructure;

namespace Inbox.IntegrationTests;

/// <summary>
/// What happens to a file an agent sends: the bytes are validated like an inbound file, the provider
/// gets the upload through the two-call sequence (upload, then a message whose payload references the
/// upload by id), and a provider refusal or a rejected file lands on the stored message rather than
/// being thrown.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class OutboundMediaTests
{
    private readonly ApiFactory factory;

    public OutboundMediaTests(ApiFactory factory)
    {
        this.factory = factory;

        // Same reasoning as the inbound media tests: the stub graph host lives for the whole run, so
        // the upload queue, the failures and any pinned bytes are cleared per test.
        StubGraph.Handler.Reset();
    }

    [Fact]
    public async Task An_image_with_a_caption_is_uploaded_sent_and_served_back()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        var message = await SendAsync(
            admin,
            conversation.Id,
            StubGraphHandler.DefaultImage,
            "image/png",
            "itinerary.png",
            "your itinerary");

        message.Type.Should().Be("Image");
        message.Text.Should().Be("your itinerary");
        message.DeliveryStatus.Should().Be("Sent");
        message.ExternalMessageId.Should().StartWith("wamid.");

        var attachment = message.Attachments.Should().ContainSingle().Subject;
        attachment.Availability.Should().Be("Available");
        attachment.ContentType.Should().Be("image/png");
        attachment.FileName.Should().Be("itinerary.png");
        attachment.SizeBytes.Should().Be(StubGraphHandler.DefaultImage.Length);

        // Two calls at the provider: the upload, then the message referencing the upload's id.
        var upload = channel.Uploads.Should().ContainSingle().Subject;
        upload.ContentType.Should().Be("image/png");
        upload.FileName.Should().Be("itinerary.png");
        upload.Content.Should().Equal(StubGraphHandler.DefaultImage);

        channel.LastSend!.Type.Should().Be("image");
        channel.LastSend.ReferencedMediaId.Should().Be(upload.Id);

        var download = await admin.Http.GetAsync($"/api/attachments/{attachment.Id}");

        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await download.Content.ReadAsByteArrayAsync()).Should().Equal(StubGraphHandler.DefaultImage);
    }

    [Fact]
    public async Task A_document_without_a_caption_shows_its_name_on_the_thread()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        var message = await SendAsync(
            admin,
            conversation.Id,
            Encoding.ASCII.GetBytes("%PDF-1.4\n"),
            "application/pdf",
            "invoice-2026-03.pdf");

        // No caption, so the thread says what was sent in the same words inbound media uses.
        message.Type.Should().Be("Document");
        message.Text.Should().Be("[document: invoice-2026-03.pdf]");

        var attachment = message.Attachments.Should().ContainSingle().Subject;
        attachment.Availability.Should().Be("Available");
        attachment.ContentType.Should().Be("application/pdf");
        attachment.FileName.Should().Be("invoice-2026-03.pdf");

        // A document carries its file name to the recipient; the caption is absent, not empty.
        channel.LastSend!.Type.Should().Be("document");
        channel.LastSend.ReferencedMediaId.Should().Be(channel.LastUpload!.Id);
        channel.LastSend.Payload.GetProperty("document").TryGetProperty("caption", out _).Should().BeFalse();
        channel.LastSend.Payload.GetProperty("document").GetProperty("filename").GetString()
            .Should().Be("invoice-2026-03.pdf");
    }

    [Fact]
    public async Task An_audio_file_is_refused_before_anything_is_stored_or_sent()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        // Real MP3 header bytes so the file is genuinely classified as audio before the refusal.
        var voiceNote = Encoding.ASCII.GetBytes("ID3\x03\x00\x00\x00\x00\x00\x00");

        var response = await admin.PostMultipartAsync(
            $"/api/conversations/{conversation.Id}/messages",
            "note.mp3",
            voiceNote,
            "audio/mpeg");

        // Images and documents are the agreed outbound scope; audio is inbound-only, and the refusal
        // happens before anything is written or offered to the provider.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be("conversation.media_rejected");
        (await ApiClient.ReadProblemAsync(response)).Detail.Should().Contain("Audio");

        channel.Uploads.Should().BeEmpty();
        channel.LastSend.Should().BeNull();
    }

    [Fact]
    public async Task A_document_over_the_outbound_ceiling_is_refused()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        // 17 MB with a real PDF header: inside the 100 MB a customer's document may use on its way
        // to storage, over the tighter ceiling a send holds in memory, so it is the outbound bound -
        // not the storage bound - that refuses it.
        var oversized = new byte[17 * 1024 * 1024];
        Encoding.ASCII.GetBytes("%PDF-1.4\n").CopyTo(oversized, 0);

        var response = await admin.PostMultipartAsync(
            $"/api/conversations/{conversation.Id}/messages",
            "contracts.pdf",
            oversized,
            "application/pdf");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be("conversation.media_rejected");
        (await ApiClient.ReadProblemAsync(response)).Detail.Should().Contain("16 MB");

        channel.Uploads.Should().BeEmpty();
        channel.LastSend.Should().BeNull();
    }

    [Fact]
    public async Task A_provider_refusal_of_the_upload_lands_on_the_stored_message()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        // The upload leg is refused first: nothing was accepted, so the send leg never runs and the
        // message is stored with the provider's own words.
        StubGraph.Handler.FailNext();

        var message = await SendAsync(
            admin,
            conversation.Id,
            StubGraphHandler.DefaultImage,
            "image/png",
            "itinerary.png");

        message.DeliveryStatus.Should().Be("Failed");
        message.FailureReason.Should().Contain("24 hours");

        channel.Uploads.Should().BeEmpty();
        channel.LastSend.Should().BeNull();
    }

    [Fact]
    public async Task A_file_the_storage_layer_refuses_is_not_stored_or_sent()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        // Markup masquerading as an image: the same rule that refuses an inbound file refuses this one,
        // before anything is written or offered to the provider.
        var response = await admin.PostMultipartAsync(
            $"/api/conversations/{conversation.Id}/messages",
            "liar.png",
            Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>not an image</body></html>"),
            "image/png");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be("conversation.media_rejected");

        channel.Uploads.Should().BeEmpty();
        channel.LastSend.Should().BeNull();
    }

    [Fact]
    public async Task An_oversized_file_is_refused_before_it_reaches_the_provider()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        // Over the image ceiling: a PNG of 6 MB exceeds the 5 MB Meta allows, and our own ceiling is
        // the number the provider holds us to. The signature is a real one - all eight PNG bytes -
        // so the file is classified as an image and refused for its size, not for an unrecognised
        // header that would have been refused at any size.
        var oversized = new byte[6 * 1024 * 1024];
        oversized[0] = 0x89;
        oversized[1] = 0x50;
        oversized[2] = 0x4E;
        oversized[3] = 0x47;
        oversized[4] = 0x0D;
        oversized[5] = 0x0A;
        oversized[6] = 0x1A;
        oversized[7] = 0x0A;

        var response = await admin.PostMultipartAsync(
            $"/api/conversations/{conversation.Id}/messages",
            "big.png",
            oversized,
            "image/png");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be("conversation.media_rejected");

        channel.Uploads.Should().BeEmpty();
        channel.LastSend.Should().BeNull();
    }

    [Fact]
    public async Task A_caption_longer_than_a_thousand_characters_is_refused()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        var response = await admin.PostMultipartAsync(
            $"/api/conversations/{conversation.Id}/messages",
            "itinerary.png",
            StubGraphHandler.DefaultImage,
            "image/png",
            new string('a', 1025));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be("conversation.media_caption_too_long");

        channel.Uploads.Should().BeEmpty();
        channel.LastSend.Should().BeNull();
    }

    [Fact]
    public async Task An_empty_file_is_refused_without_touching_the_provider()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

var response = await admin.PostMultipartAsync(
            $"/api/conversations/{conversation.Id}/messages",
            "empty.png",
            [],
            "image/png");

        // The controller rejects a zero-length upload outright, before it is worth storing anything.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be("conversation.media_missing");

        channel.Uploads.Should().BeEmpty();
        channel.LastSend.Should().BeNull();
    }

    [Fact]
    public async Task A_media_send_outside_the_reply_window_is_refused()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(
            factory,
            namePrefix: "Stale sender",
            occurredAtUtc: DateTime.UtcNow.AddDays(-2));

        var response = await admin.PostMultipartAsync(
            $"/api/conversations/{conversation.Id}/messages",
            "itinerary.png",
            StubGraphHandler.DefaultImage,
            "image/png",
            "your itinerary");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be("conversation.window_closed");

        channel.Uploads.Should().BeEmpty();
        channel.LastSend.Should().BeNull();
    }

    [Fact]
    public async Task A_media_reply_from_a_second_agent_records_that_they_joined()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var owner = await AgentScenarios.CreateAsync(admin.Client);
        var joiner = await AgentScenarios.CreateAsync(admin.Client);
        var ownerSession = await ApiClient.LoginAsync(factory, owner.Email, owner.TemporaryPassword);
        var joinerSession = await ApiClient.LoginAsync(factory, joiner.Email, joiner.TemporaryPassword);
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        await ConversationScenarios.ActionAsync(ownerSession.Client, conversation.Id, "take");

        var response = await joinerSession.Client.PostMultipartAsync(
            $"/api/conversations/{conversation.Id}/messages",
            "itinerary.png",
            StubGraphHandler.DefaultImage,
            "image/png");

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var stored = await ConversationScenarios.OpenAsync(ownerSession.Client, conversation.Id);

        // A file send joins the thread exactly like a text reply: joining is not taking, so the
        // thread stays with the agent who owns it while the history says who spoke on it. That the
        // send also reached the provider is what makes this a join that happened, not a claim.
        stored.Events.Should().ContainSingle(x => x.Type == "Joined" && x.ActorAgentId == joiner.Id);
        stored.AssignedAgentId.Should().Be(owner.Id);

        channel.Uploads.Should().ContainSingle();
    }

    private async Task<ConversationMessageResponse> SendAsync(
        ApiClient client,
        Guid conversationId,
        byte[] content,
        string contentType,
        string fileName,
        string? caption = null)
    {
        var response = await client.PostMultipartAsync(
            $"/api/conversations/{conversationId}/messages",
            fileName,
            content,
            contentType,
            caption);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return await client.ReadAsync<ConversationMessageResponse>(response);
    }
}
