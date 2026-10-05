using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Inbox.Application.Channels;
using Inbox.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inbox.Infrastructure.Channels.WhatsApp;

/// <summary>
/// The thin HTTP client for the WhatsApp Cloud API. One POST per send, one method per payload type,
/// and no retry logic: a failed reply is stored on the message and an agent decides what to do, so
/// silently resending could duplicate a message the customer already received.
/// </summary>
public sealed class WhatsAppGraphClient(
    HttpClient http,
    IOptions<MetaOptions> options,
    ILogger<WhatsAppGraphClient> logger)
{
    private readonly MetaOptions _options = options.Value;

    /// <summary>
    /// Sends a free-form text message. WhatsApp accepts it only inside the customer's 24-hour
    /// window; outside it the provider answers with an error code, which is recorded on the message.
    /// </summary>
    public async Task<SendResult> SendTextAsync(
        string phoneNumberId,
        string accessToken,
        string recipient,
        string text,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            to = recipient,
            type = "text",
            text = new { preview_url = false, body = text }
        };

        return await PostAsync(phoneNumberId, accessToken, payload, cancellationToken);
    }

    /// <summary>
    /// Sends an approved template. This is the only message WhatsApp accepts outside the window, and
    /// the parameters are positional because that is how a template is submitted for approval.
    /// </summary>
    public async Task<SendResult> SendTemplateAsync(
        string phoneNumberId,
        string accessToken,
        string recipient,
        TemplateMessage template,
        CancellationToken cancellationToken)
    {
        var language = new { code = template.LanguageCode };

        // Two payload shapes rather than a null "components": serialising null would send the field
        // with no value, which the provider rejects for a body-less template.
        object payload = template.BodyParameters.Count == 0
            ? new
            {
                messaging_product = "whatsapp",
                to = recipient,
                type = "template",
                template = new { name = template.Name, language }
            }
            : new
            {
                messaging_product = "whatsapp",
                to = recipient,
                type = "template",
                template = new
                {
                    name = template.Name,
                    language,
                    components = new object[]
                    {
                        new
                        {
                            type = "body",
                            parameters = template.BodyParameters.Select(value => new { type = "text", text = value }).ToArray()
                        }
                    }
                }
            };

        return await PostAsync(phoneNumberId, accessToken, payload, cancellationToken);
    }

    private async Task<SendResult> PostAsync(
        string phoneNumberId,
        string accessToken,
        object payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.GraphApiVersion))
        {
            // Sending is impossible without it, and the failure should name the setting instead of
            // producing a confusing 404 from the graph host.
            return SendResult.Failed(
                "Meta:GraphApiVersion is not configured, so the WhatsApp API version is unknown.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesPath(phoneNumberId))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        // The token authenticates this call and must never appear in a log line or an exception.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The recipient is the customer's number, which is personal data: it belongs in the
            // provider's own logs, not in an application log that may be shipped around.
            logger.LogWarning(exception, "Could not reach the WhatsApp API to send a message.");

            return SendResult.Failed("The WhatsApp API could not be reached.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var messageId = ReadMessageId(body);

                if (messageId is null)
                {
                    // A 200 without an id cannot be correlated with a status webhook later, so it is
                    // treated as a failure rather than as a send with no id.
                    logger.LogWarning("The WhatsApp API accepted a message but returned no id.");

                    return SendResult.Failed("The WhatsApp API accepted the message but returned no message id.");
                }

                return SendResult.Sent(messageId);
            }

            var reason = DescribeFailure(response.StatusCode, body);

            logger.LogWarning("WhatsApp refused a message with {StatusCode}: {Reason}", (int)response.StatusCode, reason);

            return SendResult.Failed(reason);
        }
    }

    private string MessagesPath(string phoneNumberId) =>
        $"{_options.GraphBaseUrl.TrimEnd('/')}/{_options.VersionSegment}/{phoneNumberId}/messages";

    /// <summary>Reads <c>messages[0].id</c> from a send response.</summary>
    private static string? ReadMessageId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.TryGetProperty("messages", out var messages)
                   && messages.ValueKind == JsonValueKind.Array
                   && messages.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first
                   && first.TryGetProperty("id", out var id)
                   && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns a provider error into one sentence for the message history. The raw body is logged, not
    /// stored: it can contain the recipient's number, and the message history is read by agents.
    /// </summary>
    private static string DescribeFailure(HttpStatusCode status, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var value) ? value.GetString() : null;
                var code = error.TryGetProperty("code", out var codeValue) ? codeValue.GetInt32() : (int?)null;
                var subcode = error.TryGetProperty("error_subcode", out var subcodeValue) ? subcodeValue.GetInt32() : (int?)null;

                return string.Join(
                    ": ",
                    $"WhatsApp API returned {(int)status}",
                    code is null ? null : $"code {code}",
                    subcode is null ? null : $"subcode {subcode}",
                    message ?? "no message");
            }
        }
        catch (JsonException)
        {
            // Fall through: a non-JSON body is more useful reported as the status code alone.
        }

        return $"WhatsApp API returned {(int)status}.";
    }
}