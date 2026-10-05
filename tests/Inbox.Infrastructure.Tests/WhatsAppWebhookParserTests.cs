using FluentAssertions;
using Inbox.Application.Channels;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Channels.WhatsApp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Inbox.Infrastructure.Tests;

/// <summary>
/// The parser is fed recorded Meta payloads rather than a hand-built object graph: the envelope is
/// the part most likely to drift from the documentation, and a test written against the same shape
/// would simply agree with the bug.
/// </summary>
public sealed class WhatsAppWebhookParserTests
{
    private const string PhoneNumberId = "PHONE_NUMBER_ID";

    private static readonly Channel Channel = new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Type = ChannelType.WhatsApp,
        ExternalAccountId = PhoneNumberId
    };

    private static readonly DateTimeOffset Now = new(2024, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static WhatsAppWebhookParser CreateParser() =>
        new(new FakeTimeProvider(Now), NullLogger<WhatsAppWebhookParser>.Instance);

    [Fact]
    public void A_text_message_becomes_one_inbound_event()
    {
        var events = CreateParser().Parse(Fixtures.Read("whatsapp-text-message.json"), Channel);

        var message = events.Should().ContainSingle().Subject;
        message.Kind.Should().Be(InboundEventKind.Message);
        message.ChannelId.Should().Be(Channel.Id);
        message.ChannelType.Should().Be(ChannelType.WhatsApp);
        message.ExternalContactId.Should().Be("96550001111");
        message.ContactDisplayName.Should().Be("Nadia");
        message.ExternalMessageId.Should().Be("wamid.HBgLMTY1MDM4NDA1NTU2NDIzNDgwMAkFVBQ");
        message.Type.Should().Be(MessageType.Text);
        message.Text.Should().Be("I need a quote for a flight to London");
        message.Status.Should().BeNull();
    }

    [Fact]
    public void The_provider_timestamp_is_read_as_utc_seconds()
    {
        var events = CreateParser().Parse(Fixtures.Read("whatsapp-text-message.json"), Channel);

        var expected = DateTimeOffset.FromUnixTimeSeconds(1730000000).UtcDateTime;

        events.Should().ContainSingle().Subject.OccurredAtUtc.Should().Be(expected);
    }

    [Fact]
    public void A_missing_timestamp_falls_back_to_arrival_time()
    {
        var body = """
        {
          "object": "whatsapp_business_account",
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PHONE_NUMBER_ID" },
            "messages": [{ "from": "96550001111", "id": "wamid.NO_TS", "type": "text", "text": { "body": "hi" } }]
          } }] }]
        }
        """;

        CreateParser().Parse(body, Channel).Should().ContainSingle()
            .Subject.OccurredAtUtc.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public void Media_is_labelled_so_the_conversation_preview_is_not_empty()
    {
        var events = CreateParser().Parse(Fixtures.Read("whatsapp-image-message.json"), Channel);

        var message = events.Should().ContainSingle().Subject;
        message.Type.Should().Be(MessageType.Image);

        // The caption is the only text a media message carries, and it is what the agent sees.
        message.Text.Should().Be("the itinerary");
    }

    [Fact]
    public void A_delivery_status_becomes_a_status_event()
    {
        var events = CreateParser().Parse(Fixtures.Read("whatsapp-status-delivered.json"), Channel);

        var status = events.Should().ContainSingle().Subject;
        status.Kind.Should().Be(InboundEventKind.StatusUpdate);
        status.Status.Should().Be(DeliveryStatus.Delivered);
        status.ExternalMessageId.Should().Be("wamid.HBgLMTY1MDM4NDU5ODc2NTQzMjEwMA");
        status.ExternalContactId.Should().Be("96550001111");
        status.Text.Should().BeNull();
    }

    [Fact]
    public void A_read_status_is_mapped()
    {
        var events = CreateParser().Parse(Fixtures.Read("whatsapp-status-read.json"), Channel);

        events.Should().ContainSingle().Subject.Status.Should().Be(DeliveryStatus.Read);
    }

    [Fact]
    public void A_failure_keeps_the_reason_meta_gave()
    {
        var events = CreateParser().Parse(Fixtures.Read("whatsapp-status-failed.json"), Channel);

        var status = events.Should().ContainSingle().Subject;
        status.Status.Should().Be(DeliveryStatus.Failed);
        status.StatusDetail.Should().Contain("Re-engagement message");
        status.StatusDetail.Should().Contain("131047");
    }

    [Fact]
    public void A_status_the_domain_does_not_model_is_dropped_rather_than_mapped()
    {
        var body = """
        {
          "entry": [{ "changes": [{ "value": {
            "metadata": { "phone_number_id": "PHONE_NUMBER_ID" },
            "statuses": [{ "id": "wamid.GONE", "status": "deleted", "timestamp": "1730000200", "recipient_id": "96550001111" }]
          } }] }]
        }
        """;

        // Mapping "deleted" onto Read or Failed would put a lie in the message history.
        CreateParser().Parse(body, Channel).Should().BeEmpty();
    }

    [Fact]
    public void An_unmodelled_message_kind_is_stored_as_unsupported_instead_of_being_dropped()
    {
        var events = CreateParser().Parse(Fixtures.Read("whatsapp-envelope-error.json"), Channel);

        // The thread stays complete even though the dashboard cannot render a button reply yet.
        var message = events.Should().ContainSingle().Subject;
        message.Type.Should().Be(MessageType.Unsupported);
        message.Text.Should().Be("[interactive]");
    }

    [Fact]
    public void A_payload_without_the_expected_shape_yields_nothing_rather_than_throwing()
    {
        CreateParser().Parse("not json at all", Channel).Should().BeEmpty();
        CreateParser().Parse("{}", Channel).Should().BeEmpty();
        CreateParser().Parse("""{"entry":[] }""", Channel).Should().BeEmpty();
    }

    [Fact]
    public void The_phone_number_id_is_read_without_parsing_the_whole_payload()
    {
        WhatsAppWebhookParser.ReadPhoneNumberId(Fixtures.Read("whatsapp-text-message.json"))
            .Should().Be(PhoneNumberId);

        WhatsAppWebhookParser.ReadPhoneNumberId("""{"entry":[{"changes":[{"value":{}}]}]}""")
            .Should().BeNull();

        WhatsAppWebhookParser.ReadPhoneNumberId("garbage").Should().BeNull();
    }

    [Fact]
    public void Several_messages_in_one_delivery_become_several_events()
    {
        var body = """
        {
          "entry": [{ "changes": [{ "value": {
            "metadata": { "phone_number_id": "PHONE_NUMBER_ID" },
            "contacts": [
              { "profile": { "name": "Nadia" }, "wa_id": "96550001111" },
              { "profile": { "name": "Omar" }, "wa_id": "96550002222" }
            ],
            "messages": [
              { "from": "96550001111", "id": "wamid.ONE", "timestamp": "1730000000", "type": "text", "text": { "body": "first" } },
              { "from": "96550002222", "id": "wamid.TWO", "timestamp": "1730000001", "type": "text", "text": { "body": "second" } }
            ]
          } }] }]
        }
        """;

        var events = CreateParser().Parse(body, Channel);

        events.Should().HaveCount(2);

        // Names are matched per wa_id, so batching two contacts cannot swap their names.
        events[0].ContactDisplayName.Should().Be("Nadia");
        events[1].ContactDisplayName.Should().Be("Omar");
    }
}