using System.Net;
using FluentAssertions;
using Inbox.Application.Conversations;
using Inbox.Infrastructure.Conversations;
using Inbox.IntegrationTests.Infrastructure;

namespace Inbox.IntegrationTests;

/// <summary>
/// A template is the only message WhatsApp accepts outside the customer's 24-hour window, so the
/// window rules are the point of this endpoint: inside it a template still sends, outside it a plain
/// message cannot. The provider is stubbed, so what is proved here is what we send and how we record
/// a refusal.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class TemplateMessageTests(ApiFactory factory)
{
    [Fact]
    public async Task A_template_is_sent_with_its_positional_parameters()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        var message = await SendTemplateAsync(
            admin.Client,
            conversation.Id,
            new SendTemplateRequest("follow_up_quote", "en_US", ["Amir", "245 USD"]));

        message.Type.Should().Be("Template");
        message.DeliveryStatus.Should().Be("Sent");

        // The stored preview is what an agent needs when reading the thread later: the provider
        // renders the real message, the history keeps the words that were sent.
        message.Text.Should().Be("follow_up_quote: Amir, 245 USD");

        var sent = channel.LastSend!;
        sent.Type.Should().Be("template");
        sent.Recipient.Should().Be(conversation.Contact.ExternalId);
        sent.TemplateName.Should().Be("follow_up_quote");
        sent.TemplateParameters.Should().Equal("Amir", "245 USD");

        // The token authenticates the call and must never end up in the payload or the log.
        sent.AccessToken.Should().Be("EAA_meta_access_token_value");

        (await ConversationScenarios.MessagesAsync(admin.Client, conversation.Id)).Items
            .Should().ContainSingle(x => x.Id == message.Id && x.Direction == "Outbound");
    }

    [Fact]
    public async Task A_template_without_parameters_sends_no_components()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        var message = await SendTemplateAsync(
            admin.Client,
            conversation.Id,
            new SendTemplateRequest("opt_in_reminder", "en_US", []));

        message.Text.Should().Be("opt_in_reminder");

        // Serialising an empty components array is rejected by the provider, so the field has to be
        // absent rather than empty.
        var sent = channel.LastSend!;
        sent.TemplateName.Should().Be("opt_in_reminder");
        sent.Payload.GetProperty("template").TryGetProperty("components", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_template_is_the_only_way_to_reach_a_customer_after_the_window_closed()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (_, conversation) = await WhatsAppScenarios.DeliverAsync(factory, occurredAtUtc: DateTime.UtcNow.AddHours(-25));

        conversation.ReplyWindowOpen.Should().BeFalse();

        var plain = await admin.Client.PostAsync(
            $"/api/conversations/{conversation.Id}/messages",
            new SendMessageRequest("Still there?"));

        plain.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(plain)).ErrorCode.Should().Be(ConversationErrors.WindowClosed);

        // The template does not care about the window: it was approved for exactly this case.
        var template = await SendTemplateAsync(
            admin.Client,
            conversation.Id,
            new SendTemplateRequest("follow_up_quote", "en_US", ["Amir"]));

        template.DeliveryStatus.Should().Be("Sent");
    }

    [Fact]
    public async Task A_refused_template_is_stored_as_failed_with_the_reason_from_Meta()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (_, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        // This is what happens when a template is not approved for this number, which is the failure
        // an agent has to see rather than a silent 500.
        StubGraph.Handler.Reset();
        StubGraph.Handler.FailNext();

        var response = await admin.Client.PostAsync(
            $"/api/conversations/{conversation.Id}/template-messages",
            new SendTemplateRequest("not_approved_yet", "en_US", []));

        // The send is attempted and recorded, not rejected by us, so the caller still gets the message.
        var message = await admin.Client.ReadAsync<ConversationMessageResponse>(response);

        message.DeliveryStatus.Should().Be("Failed");
        message.FailureReason.Should().Contain("24 hours");

        var stored = (await ConversationScenarios.MessagesAsync(admin.Client, conversation.Id)).Items
            .Single(x => x.Id == message.Id);

        stored.DeliveryStatus.Should().Be("Failed");
        stored.FailureReason.Should().Contain("24 hours");
    }

    [Fact]
    public async Task An_unreachable_graph_host_is_recorded_as_a_failure_not_an_exception()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (_, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        StubGraph.Handler.Reset();
        StubGraph.Handler.FailNextWithUnreadableBody();

        var response = await admin.Client.PostAsync(
            $"/api/conversations/{conversation.Id}/template-messages",
            new SendTemplateRequest("follow_up_quote", "en_US", ["Amir"]));

        var message = await admin.Client.ReadAsync<ConversationMessageResponse>(response);

        // An error body that is not the provider's JSON is reported by status code. The recipient's
        // number is not in it, and it must not end up on the message history either.
        message.DeliveryStatus.Should().Be("Failed");
        message.FailureReason.Should().Contain("502");
    }

    [Fact]
    public async Task An_empty_or_unnamed_template_is_a_validation_error()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (_, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        var nameless = await admin.Client.PostAsync(
            $"/api/conversations/{conversation.Id}/template-messages",
            new SendTemplateRequest("  ", "en_US", []));

        nameless.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var languageLess = await admin.Client.PostAsync(
            $"/api/conversations/{conversation.Id}/template-messages",
            new SendTemplateRequest("follow_up_quote", "", []));

        languageLess.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Sending_a_template_requires_a_token()
    {
        var anonymous = ApiClient.Anonymous(factory);
        var missing = Guid.NewGuid();

        var response = await anonymous.PostAsync(
            $"/api/conversations/{missing}/template-messages",
            new SendTemplateRequest("follow_up_quote", "en_US", []));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<ConversationMessageResponse> SendTemplateAsync(
        ApiClient client,
        Guid conversationId,
        SendTemplateRequest request) =>
        await client.ReadAsync<ConversationMessageResponse>(
            await client.PostAsync($"/api/conversations/{conversationId}/template-messages", request));
}