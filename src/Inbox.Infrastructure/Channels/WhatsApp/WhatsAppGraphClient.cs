using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Inbox.Application.Channels;
using Inbox.Application.Options;
using Inbox.Domain.Common;
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

    /// <summary>
    /// Downloads the bytes behind a media id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two calls, not one, and the order matters. <c>GET /{media_id}</c> returns a short-lived download
    /// URL - valid for five minutes - and Meta refuses to serve the file from that URL unless the same
    /// access token is presented, so a plain GET of it fails. Both requests therefore carry the bearer
    /// token and both go through this client.
    /// </para>
    /// <para>
    /// This is the reason media is fetched while the webhook is being processed rather than later: the
    /// id itself only stays resolvable for about a week, and the URL for five minutes.
    /// </para>
    /// <para>
    /// The bytes are capped at <paramref name="maxBytes"/>. Meta reports a size we cross-check, but the
    /// cap is what actually stops a large response from being buffered, so it is enforced while reading
    /// rather than after.
    /// </para>
    /// </remarks>
    public async Task<Result<DownloadedMedia>> DownloadMediaAsync(
        string phoneNumberId,
        string accessToken,
        InboundMedia media,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.GraphApiVersion))
        {
            return Result.Failure<DownloadedMedia>(
                Error.Failure(MediaDownloadErrors.NotConfigured, "Meta:GraphApiVersion is not configured, so the media cannot be fetched."));
        }

        var referencePath =
            $"{_options.GraphBaseUrl.TrimEnd('/')}/{_options.VersionSegment}/{Uri.EscapeDataString(media.MediaId)}";

        string body;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, referencePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await http.SendAsync(request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // 404 is the common one and is usually temporary: the id is not resolvable yet, or the
                // media belongs to a phone number that is not ours. Both are worth another attempt.
                logger.LogWarning(
                    "Could not resolve WhatsApp media {MediaId}: the API returned {StatusCode}.",
                    media.MediaId,
                    (int)response.StatusCode);

                return Result.Failure<DownloadedMedia>(
                    Error.Failure(MediaDownloadErrors.Unavailable, $"WhatsApp could not resolve the media ({response.StatusCode})."));
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Could not reach the WhatsApp API to resolve a media reference.");

            return Result.Failure<DownloadedMedia>(
                Error.Failure(MediaDownloadErrors.ProviderUnreachable, "The WhatsApp API could not be reached."));
        }

        if (!TryReadDownloadUrl(body, out var downloadUrl, out var declaredContentType, out var sha256, out var fileSize))
        {
            return Result.Failure<DownloadedMedia>(
                Error.Failure(MediaDownloadErrors.Unavailable, "WhatsApp returned no download URL for the media."));
        }

        // The provider has already told us how big this is, so a file over the ceiling costs one
        // request instead of a transfer. The capped read below still enforces it, because a size that
        // is absent, wrong or shrunk after the fact would otherwise slip through.
        if (fileSize > maxBytes)
        {
            logger.LogInformation(
                "WhatsApp media {MediaId} is {FileSize} bytes, over this deployment's {Limit}-byte ceiling.",
                media.MediaId,
                fileSize.Value,
                maxBytes);

            return Result.Failure<DownloadedMedia>(
                Error.Failure(
                    MediaDownloadErrors.TooLarge,
                    $"The file is {fileSize.Value} bytes, which is over the {maxBytes}-byte limit this deployment accepts."));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Could not download WhatsApp media {MediaId}: the CDN returned {StatusCode}.",
                    media.MediaId,
                    (int)response.StatusCode);

                return Result.Failure<DownloadedMedia>(
                    Error.Failure(MediaDownloadErrors.Unavailable, $"The media download was refused ({response.StatusCode})."));
            }

            var content = await ReadCappedAsync(response, maxBytes, cancellationToken);

            if (content is null)
            {
                return Result.Failure<DownloadedMedia>(
                    Error.Failure(
                        MediaDownloadErrors.TooLarge,
                        $"The file is over the {maxBytes}-byte limit this deployment accepts."));
            }

            // Cross-checked, not trusted: a mismatch means the transfer was truncated or altered, and the
            // caller stores the bytes only after the storage layer has sniffed them.
            var effectiveSha = string.IsNullOrWhiteSpace(sha256) ? media.Sha256 : sha256;

            if (!string.IsNullOrWhiteSpace(effectiveSha) && !Matches(effectiveSha, content))
            {
                logger.LogWarning(
                    "WhatsApp media {MediaId} did not match the digest the provider advertised.",
                    media.MediaId);

                return Result.Failure<DownloadedMedia>(
                    Error.Failure(MediaDownloadErrors.Corrupt, "The downloaded media did not match its advertised digest."));
            }

            return Result.Success(new DownloadedMedia(
                content,
                declaredContentType ?? media.DeclaredContentType,
                effectiveSha));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Could not download WhatsApp media {MediaId}.", media.MediaId);

            return Result.Failure<DownloadedMedia>(
                Error.Failure(MediaDownloadErrors.ProviderUnreachable, "The media could not be downloaded."));
        }
    }

    /// <summary>
    /// Reads a response body, refusing to buffer more than <paramref name="maxBytes"/>. Returns null when
    /// the stream is over the cap, so an oversized file is never held in memory in full.
    /// </summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        var declaredLength = response.Content.Headers.ContentLength;

        if (declaredLength is > 0 && declaredLength > maxBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        using var buffer = new MemoryStream();

        var chunk = new byte[81920];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);

            if (read <= 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>
    /// Compares downloaded bytes against the digest the provider advertised. Meta publishes sha256 as hex;
    /// a base64 digest is accepted too, because that is the other form the value travels in.
    /// </summary>
    private static bool Matches(string expectedSha256, byte[] content)
    {
        var actual = Convert.ToHexString(SHA256.HashData(content));

        if (expectedSha256.Length == 64 && expectedSha256.All(Uri.IsHexDigit))
        {
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(content),
                Convert.FromBase64String(expectedSha256));
        }
        catch (FormatException)
        {
            // A digest in neither form is not something to assert on. The bytes are still validated by
            // signature before they are stored, so refusing here would only cause a spurious retry.
            return true;
        }
    }

    private static bool TryReadDownloadUrl(
        string body,
        out string url,
        out string? contentType,
        out string? sha256,
        out long? fileSize)
    {
        url = string.Empty;
        contentType = null;
        sha256 = null;
        fileSize = null;

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            url = Text(document.RootElement, "url") ?? string.Empty;
            contentType = Text(document.RootElement, "mime_type");
            sha256 = Text(document.RootElement, "sha256");
            fileSize = Number(document.RootElement, "file_size");

            return Uri.TryCreate(url, UriKind.Absolute, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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