using System.Net;
using System.Text;
using FluentAssertions;
using Inbox.Application.Channels;
using Inbox.Application.Options;
using Inbox.Infrastructure.Channels.WhatsApp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbox.Infrastructure.Tests;

/// <summary>
/// The Graph client is tested against a stubbed HTTP handler: what matters is the request it builds
/// and how it reads the response, not that Meta is reachable.
/// </summary>
public sealed class WhatsAppGraphClientTests
{
    private const string PhoneNumberId = "1234567890";
    private const string Token = "EAAG-token-value";

    private const string SuccessBody = """
    {
      "messaging_product": "whatsapp",
      "contacts": [{ "input": "96550001111", "wa_id": "96550001111" }],
      "messages": [{ "id": "wamid.SENT" }]
    }
    """;

    [Fact]
    public async Task A_text_send_posts_the_documented_payload_and_returns_the_message_id()
    {
        var request = new StubHandler(HttpStatusCode.OK, SuccessBody);
        var client = CreateClient(request, "v21.0");

        var result = await client.SendTextAsync(PhoneNumberId, Token, "96550001111", "hello", default);

        result.Success.Should().BeTrue();
        result.ExternalMessageId.Should().Be("wamid.SENT");

        request.LastRequest!.Method.Should().Be(HttpMethod.Post);
        request.LastRequest.RequestUri!.ToString()
            .Should().Be($"https://graph.example.com/v21.0/{PhoneNumberId}/messages");
        request.LastRequest.Headers.Authorization!.ToString().Should().Be($"Bearer {Token}");

        var body = request.LastBody;
        body.Should().Contain("\"messaging_product\":\"whatsapp\"");
        body.Should().Contain("\"to\":\"96550001111\"");
        body.Should().Contain("\"type\":\"text\"");
        body.Should().Contain("\"body\":\"hello\"");
    }

    [Fact]
    public async Task A_template_send_maps_body_parameters_positionally()
    {
        var request = new StubHandler(HttpStatusCode.OK, SuccessBody);
        var client = CreateClient(request, "v21.0");

        var result = await client.SendTemplateAsync(
            PhoneNumberId,
            Token,
            "96550001111",
            new TemplateMessage("booking_confirmation", "en", ["Nadia", "2026-04-02"]),
            default);

        result.Success.Should().BeTrue();
        request.LastBody.Should().Contain("\"type\":\"template\"");
        request.LastBody.Should().Contain("\"name\":\"booking_confirmation\"");
        request.LastBody.Should().Contain("\"code\":\"en\"");
        request.LastBody.Should().Contain("\"text\":\"Nadia\"");
        request.LastBody.Should().Contain("\"text\":\"2026-04-02\"");
    }

    [Fact]
    public async Task A_template_without_parameters_sends_no_components_at_all()
    {
        var request = new StubHandler(HttpStatusCode.OK, SuccessBody);
        var client = CreateClient(request, "v21.0");

        await client.SendTemplateAsync(
            PhoneNumberId,
            Token,
            "96550001111",
            new TemplateMessage("no_parameters", "ar", []),
            default);

        request.LastBody.Should().NotContain("components");
    }

    [Fact]
    public async Task A_provider_error_is_becomes_a_stored_failure_with_its_code_and_subcode()
    {
        const string errorBody = """
        {
          "error": {
            "message": "(#131047) Re-engagement message",
            "type": "OAuthException",
            "code": 131047,
            "error_subcode": 2494010,
            "fbtrace_id": "Az8k2"
          }
        }
        """;

        var client = CreateClient(new StubHandler(HttpStatusCode.BadRequest, errorBody), "v21.0");

        var result = await client.SendTextAsync(PhoneNumberId, Token, "96550001111", "hello", default);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("400");
        result.FailureReason.Should().Contain("131047");
        result.FailureReason.Should().Contain("2494010");
        result.FailureReason.Should().Contain("Re-engagement message");
    }

    [Fact]
    public async Task A_non_json_error_still_reports_the_status_code()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.BadGateway, "<html>gateway</html>"), "v21.0");

        var result = await client.SendTextAsync(PhoneNumberId, Token, "96550001111", "hello", default);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("502");
    }

    [Fact]
    public async Task A_200_without_a_message_id_is_a_failure_because_no_status_can_be_matched_later()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.OK, """{"messaging_product":"whatsapp"}"""), "v21.0");

        var result = await client.SendTextAsync(PhoneNumberId, Token, "96550001111", "hello", default);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("no message id");
    }

    [Fact]
    public async Task A_transport_failure_is_reported_instead_of_thrown()
    {
        // The message is already stored when this is called; throwing here would turn a network
        // problem into a 500 on an API call that actually succeeded.
        var client = CreateClient(new ThrowingHandler(), "v21.0");

        var result = await client.SendTextAsync(PhoneNumberId, Token, "96550001111", "hello", default);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("could not be reached");
    }

    [Fact]
    public async Task Without_a_configured_graph_version_the_send_fails_and_names_the_setting()
    {
        var request = new StubHandler(HttpStatusCode.OK, SuccessBody);
        var client = CreateClient(request, graphApiVersion: string.Empty);

        var result = await client.SendTextAsync(PhoneNumberId, Token, "96550001111", "hello", default);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("Meta:GraphApiVersion");

        // Nothing was sent, so nothing should have been requested.
        request.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task The_graph_version_is_used_whether_or_not_it_carries_a_v_prefix()
    {
        var withPrefix = new StubHandler(HttpStatusCode.OK, SuccessBody);
        await CreateClient(withPrefix, "v21.0").SendTextAsync(PhoneNumberId, Token, "1", "hi", default);
        withPrefix.LastRequest!.RequestUri!.ToString().Should().Contain("/v21.0/");

        var withoutPrefix = new StubHandler(HttpStatusCode.OK, SuccessBody);
        await CreateClient(withoutPrefix, "21.0").SendTextAsync(PhoneNumberId, Token, "1", "hi", default);
        withoutPrefix.LastRequest!.RequestUri!.ToString().Should().Contain("/v21.0/");
    }

    private static WhatsAppGraphClient CreateClient(HttpMessageHandler handler, string graphApiVersion)
    {
        var http = new HttpClient(handler) { BaseAddress = null };

        var options = Options.Create(new MetaOptions
        {
            GraphApiVersion = graphApiVersion,
            GraphBaseUrl = "https://graph.example.com"
        });

        return new WhatsAppGraphClient(http, options, NullLogger<WhatsAppGraphClient>.Instance);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("The remote name could not be resolved.");
    }
}