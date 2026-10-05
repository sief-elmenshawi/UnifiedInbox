using Inbox.Application.Channels;
using Inbox.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Inbox.Infrastructure.Channels;

/// <summary>
/// The only place channel secrets are decrypted. Everything else asks this for the plaintext it needs
/// at the moment of use, so no caller has to hold a token or know the protector purpose.
///
/// <para>
/// Undecryptable ciphertext (a rotated key ring, a restored database without the keys) is logged and
/// treated as "not configured" rather than thrown: one broken channel must not take down the whole
/// app or hide the fact that the other channels still work.
/// </para>
/// </summary>
public sealed class ChannelSecrets(
    IDataProtector protector,
    ILogger<ChannelSecrets> logger) : IChannelSecrets
{
    public string? GetAccessToken(Channel channel) => Unprotect(channel, channel.EncryptedAccessToken, "access token");

    public string? GetAppSecret(Channel channel) => Unprotect(channel, channel.EncryptedAppSecret, "app secret");

    private string? Unprotect(Channel channel, string ciphertext, string description)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(ciphertext);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Could not decrypt the {Description} of channel {ChannelId}; treat it as not configured.",
                description,
                channel.Id);

            return null;
        }
    }
}