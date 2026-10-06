using System.Net;
using System.Text;
using FluentAssertions;
using Inbox.Application.Conversations;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Persistence;
using Inbox.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inbox.IntegrationTests;

/// <summary>
/// What happens to a file a customer sends: images, documents and voice notes are fetched and kept,
/// videos and stickers are recorded but never fetched, and a file we refuse still leaves a message
/// behind saying so.
/// </summary>
/// <remarks>
/// Every test here drives the real webhook with a recorded Meta payload, so the download happens in
/// the background worker under the same retry ladder the production path uses. The stub serves bytes
/// the way Meta does - a reference lookup, then a URL that needs the bearer token - because a test
/// double that handed over the file in one call would prove nothing about the two-call sequence.
/// </remarks>
[Collection(IntegrationCollection.Name)]
public sealed class InboundMediaTests
{
    private readonly ApiFactory factory;

    public InboundMediaTests(ApiFactory factory)
    {
        this.factory = factory;

        // The stub graph host is one object for the whole run and is shared with every other test, so
        // the bytes and the failure queue this class installs are cleared here rather than left for the
        // next test in this class - or the next class - to discover.
        StubGraph.Handler.Reset();
    }

    [Fact]
    public async Task An_inbound_image_is_downloaded_stored_and_served_back_byte_for_byte()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);

        var message = await DeliverMediaAsync(
            channel,
            "whatsapp-image-message.json",
            ConversationScenarios.UniqueName("Photo sender"),
            "Photo");

        message.Type.Should().Be("Image");
        message.Text.Should().Be("the itinerary");

        var attachment = message.Attachments.Should().ContainSingle().Subject;
        attachment.Availability.Should().Be("Available");
        attachment.UnavailableReason.Should().BeNull();

        // The type stored is the one the bytes are, not the one the payload claimed - the stub serves
        // a PNG, so this would be image/jpeg if the declared type had been trusted.
        attachment.ContentType.Should().Be("image/png");
        attachment.SizeBytes.Should().Be(StubGraphHandler.DefaultImage.Length);

        var download = await admin.Client.Http.GetAsync($"/api/attachments/{attachment.Id}");

        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.Should().Be("image/png");

        // Served as an attachment, never inline: a browser must not be given a chance to interpret a
        // file as a page.
        download.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");

        (await download.Content.ReadAsByteArrayAsync()).Should().Equal(StubGraphHandler.DefaultImage);

        // Both legs of the download carry the channel's token, because the URL Meta hands back is only
        // usable with one.
        var mediaCalls = StubGraph.Handler.Calls.Where(x => x.Method == HttpMethod.Get && x.Uri.AbsolutePath.Contains("media")).ToList();

        mediaCalls.Should().HaveCount(2);
        mediaCalls.Should().OnlyContain(x => x.AccessToken == "EAA_meta_access_token_value");
    }

    [Fact]
    public async Task An_inbound_document_is_stored_under_its_own_name()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);

        // The document fixture declares application/pdf, so the stub has to hand over bytes whose magic
        // really is a PDF; the wildcard default (a PNG) would only prove the mismatch path.
        StubGraph.Handler.WithMedia("media-document-1", Encoding.ASCII.GetBytes("%PDF-1.4\n"), "application/pdf");

        var message = await DeliverMediaAsync(
            channel,
            "whatsapp-document-message.json",
            ConversationScenarios.UniqueName("Invoice sender"),
            "Invoice");

        var attachment = message.Attachments.Should().ContainSingle().Subject;
        attachment.Availability.Should().Be("Available");
        attachment.FileName.Should().Be("invoice-2026-03.pdf");

        var download = await admin.Client.Http.GetAsync($"/api/attachments/{attachment.Id}");

        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentDisposition!.FileName.Should().Contain("invoice-2026-03.pdf");
    }

    [Fact]
    public async Task A_voice_note_is_stored_as_the_audio_it_is()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);

        // Opus in an Ogg container: the MIME carries a codec parameter, which has to be compared
        // without it or every voice note is rejected as an unknown type.
        StubGraph.Handler.WithMedia("media-voice-note-1", Encoding.ASCII.GetBytes("OggS-not-really"), "audio/ogg");

        var message = await DeliverMediaAsync(
            channel,
            "whatsapp-audio-message.json",
            ConversationScenarios.UniqueName("Voice sender"),
            "Voice");

        message.Type.Should().Be("Audio");

        var attachment = message.Attachments.Should().ContainSingle().Subject;
        attachment.Availability.Should().Be("Available");
        attachment.ContentType.Should().Be("audio/ogg");
        attachment.FileName.Should().Be("voice-note");
    }

    [Fact]
    public async Task A_video_is_recorded_but_never_downloaded()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);

        var message = await DeliverMediaAsync(
            channel,
            "whatsapp-video-message.json",
            ConversationScenarios.UniqueName("Video sender"),
            "Video");

        message.Type.Should().Be("Video");
        message.Text.Should().Be("the suite");

        var attachment = message.Attachments.Should().ContainSingle().Subject;
        attachment.Availability.Should().Be("NotStored");
        attachment.UnavailableReason.Should().Contain("Videos are not downloaded");

        NoMediaWasRequested("media-video-1");
    }

    [Fact]
    public async Task A_sticker_is_recorded_as_a_sticker_and_never_downloaded()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);

        var message = await DeliverMediaAsync(
            channel,
            "whatsapp-sticker-message.json",
            ConversationScenarios.UniqueName("Sticker sender"),
            "Sticker");

        // Its own type, not "Image": the customer sent a sticker, and the thread says so. Calling it an
        // image would also make it look like a file that should have been fetched and was not.
        message.Type.Should().Be("Sticker");
        message.Text.Should().Be("[sticker]");

        var attachment = message.Attachments.Should().ContainSingle().Subject;
        attachment.Availability.Should().Be("NotStored");
        attachment.UnavailableReason.Should().Contain("Stickers are not downloaded");

        NoMediaWasRequested("media-sticker-1");
    }

    [Fact]
    public async Task A_location_is_kept_as_a_message_rather_than_dropped()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);

        var message = await DeliverMediaAsync(
            channel,
            "whatsapp-location-message.json",
            ConversationScenarios.UniqueName("Location sender"),
            "Location");

        // Nothing here can be rendered or forwarded, but the customer sent something and the thread
        // must show that it arrived rather than being silent about it.
        message.Type.Should().Be("Unsupported");
        message.Text.Should().Be("[location]");
        message.Attachments.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_that_is_too_large_is_refused_and_the_message_still_arrives()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);

        // Declared over the ceiling: the reference lookup is enough to refuse it, so the transfer is
        // never made. The message must not be lost over a file we were always going to drop.
        var oversized = new byte[9 * 1024 * 1024];
        oversized[0] = 0x89;
        oversized[1] = 0x50;
        oversized[2] = 0x4E;
        oversized[3] = 0x47;

        StubGraph.Handler.WithMedia("media-1234567890", oversized);

        var message = await DeliverMediaAsync(
            channel,
            "whatsapp-image-message.json",
            ConversationScenarios.UniqueName("Oversized sender"),
            "Oversized");

        message.Type.Should().Be("Image");
        message.Text.Should().Be("the itinerary");

        var attachment = message.Attachments.Should().ContainSingle().Subject;
        attachment.Availability.Should().Be("Unavailable");

        // The reason names the limit rather than saying "failed", so an agent can tell the customer
        // which of our rules said no. SizeBytes is zero because nothing was transferred: the provider's
        // own size report was enough to refuse it in one request.
        attachment.UnavailableReason.Should().Contain("limit this deployment accepts");
        attachment.SizeBytes.Should().Be(0);

        var download = await admin.Client.Http.GetAsync($"/api/attachments/{attachment.Id}");

        // 410, not 404: the attachment is real, the caller may see it, and there is simply nothing to open.
        download.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task A_file_that_claims_a_type_is_stored_as_the_type_its_bytes_are()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);

        // Declared as a JPEG, actually a web page. The declared type is a hint from an untrusted
        // source, so the file is kept as text/plain - which is exactly what makes it harmless - and
        // never served as an image.
        StubGraph.Handler.WithMedia(
            "media-1234567890",
            Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>not an image</body></html>"),
            "image/jpeg");

        var message = await DeliverMediaAsync(
            channel,
            "whatsapp-image-message.json",
            ConversationScenarios.UniqueName("Liar sender"),
            "Liar");

        var attachment = message.Attachments.Should().ContainSingle().Subject;

        attachment.Availability.Should().Be("Available");
        attachment.ContentType.Should().Be("text/plain");

        var download = await admin.Client.Http.GetAsync($"/api/attachments/{attachment.Id}");

        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
    }

    [Fact]
    public async Task A_file_that_is_not_a_known_type_is_refused_with_a_reason()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);

        // Not any kind of file we accept: no recognisable signature, nothing textual. The message
        // still lands, with an attachment that says why there is no file.
        StubGraph.Handler.WithMedia(
            "media-1234567890",
            [0x00, 0x04, 0x39, 0xA5, 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0xFF],
            "image/jpeg");

        var message = await DeliverMediaAsync(
            channel,
            "whatsapp-image-message.json",
            ConversationScenarios.UniqueName("Binary sender"),
            "Binary");

        var attachment = message.Attachments.Should().ContainSingle().Subject;

        attachment.Availability.Should().Be("Unavailable");
        attachment.UnavailableReason.Should().Contain("could not be identified");
    }

    [Fact]
    public async Task A_media_download_that_fails_once_is_retried_and_the_message_is_not_lost()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);

        // The outbox owns the retry ladder, and the first delay is zero: a lookup that 404s once is
        // the ordinary shape of this, not a lost message.
        StubGraph.Handler.FailNextMediaLookups();

        var message = await DeliverMediaAsync(
            channel,
            "whatsapp-image-message.json",
            ConversationScenarios.UniqueName("Retried sender"),
            "Retried");

        var attachment = message.Attachments.Should().ContainSingle().Subject;
        attachment.Availability.Should().Be("Available");

        // Two failed lookups' worth of call recorded before the one that worked.
        StubGraph.Handler.Calls.Count(x => x.Method == HttpMethod.Get && x.Uri.AbsolutePath.Contains("media")).Should().BeGreaterThan(2);
    }

    [Fact]
    public async Task An_attachment_cannot_be_downloaded_without_a_token()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);

        var message = await DeliverMediaAsync(
            channel,
            "whatsapp-image-message.json",
            ConversationScenarios.UniqueName("Unauthenticated sender"),
            "Unauthenticated");

        var attachment = message.Attachments.Should().ContainSingle().Subject;

        var anonymous = await ApiClient.Anonymous(factory).Http.GetAsync($"/api/attachments/{attachment.Id}");

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_attachment_id_from_another_workspace_is_refused()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);

        var message = await DeliverMediaAsync(
            channel,
            "whatsapp-image-message.json",
            ConversationScenarios.UniqueName("Isolated sender"),
            "Isolated");

        var attachment = message.Attachments.Should().ContainSingle().Subject;
        var foreignId = await SeedAttachmentInAnotherWorkspaceAsync(attachment.Id);

        // Proves the filter is a workspace check rather than a comment: the id exists, the row is
        // readable, and it is still refused.
        foreignId.Should().NotBe(attachment.Id);

        var response = await admin.Client.Http.GetAsync($"/api/attachments/{foreignId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_attachment_id_that_does_not_exist_is_a_404()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);

        var response = await admin.Client.Http.GetAsync($"/api/attachments/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Posts a recorded media payload and waits for the message that results, which is the only point
    /// at which the download has been attempted, refused or retried.
    /// </summary>
    private async Task<ConversationMessageResponse> DeliverMediaAsync(
        WhatsAppChannel channel,
        string fixture,
        string displayName,
        string namePrefix)
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var messageId = WhatsAppScenarios.NewExternalMessageId();

        var response = await WhatsAppScenarios.PostSignedAsync(
            ApiClient.Anonymous(factory),
            channel,
            WhatsAppScenarios.Body(
                fixture,
                channel.PhoneNumberId,
                messageId,
                WhatsAppScenarios.NewExternalContactId(),
                displayName,
                DateTime.UtcNow));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var conversation = await ConversationScenarios.WaitForMessageAsync(admin.Client, displayName, messageId);
        var messages = await ConversationScenarios.MessagesAsync(admin.Client, conversation.Id);

        return messages.Items.Should().ContainSingle(x => x.ExternalMessageId == messageId).Subject;
    }

    /// <summary>Asserts the provider was never asked for a file we do not keep.</summary>
    private static void NoMediaWasRequested(string mediaId) =>
        StubGraph.Handler.Calls.Should().NotContain(x =>
            x.Method == HttpMethod.Get && x.Uri.AbsolutePath.Contains(mediaId, StringComparison.Ordinal));

    /// <summary>
    /// Writes a second workspace with its own conversation, message and attachment row. Multi-tenancy
    /// is not a feature yet, but the attachment endpoint's workspace filter has to be real and provable
    /// before it is relied on, and the only way to prove a filter is to have a row the filter excludes.
    /// </summary>
    private async Task<Guid> SeedAttachmentInAnotherWorkspaceAsync(Guid storedAttachmentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var workspaceId = Guid.NewGuid();
        var channelId = await db.Channels.Select(x => x.Id).FirstAsync();
        var contactId = Guid.NewGuid();

        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Another company", CreatedAtUtc = DateTime.UtcNow });
        db.Contacts.Add(new Contact
        {
            Id = contactId,
            WorkspaceId = workspaceId,
            DisplayName = "Someone else",
            CreatedAtUtc = DateTime.UtcNow
        });

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ChannelId = channelId,
            ContactId = contactId,
            LastMessageAtUtc = DateTime.UtcNow
        };

        db.Conversations.Add(conversation);

        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            ChannelId = channelId,
            Direction = MessageDirection.Inbound,
            SenderType = MessageSenderType.Customer,
            Type = MessageType.Image,
            Text = "a photo",
            CreatedAtUtc = DateTime.UtcNow
        };

        db.Messages.Add(message);

        // Available, with the same bytes: only the workspace differs, so a test failure here means the
        // filter is not being applied rather than the file being missing.
        var source = await db.MessageAttachments.AsNoTracking().FirstAsync(x => x.Id == storedAttachmentId);

        var attachment = MessageAttachment.Stored(
            message.Id,
            source.FileName,
            source.ContentType,
            source.SizeBytes,
            source.StoragePath,
            source.ExternalMediaId);

        db.MessageAttachments.Add(attachment);

        await db.SaveChangesAsync();

        return attachment.Id;
    }
}