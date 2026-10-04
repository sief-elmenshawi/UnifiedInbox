namespace Inbox.Application.Common;

/// <summary>
/// Opaque list cursor. It currently encodes an offset, which is deterministic and translatable
/// because the ordering is a fixed (LastMessageAtUtc desc, Id desc).
/// <para>
/// The trade-off: if new messages arrive while an agent pages backwards, an item can shift by one
/// page. That is acceptable for a shared inbox of a few thousand conversations; a strict keyset
/// cursor would need a monotonically increasing tiebreaker column on Conversations.
/// </para>
/// </summary>
public static class CursorCodec
{
    public static string Encode(int offset) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"o{offset}"));

    public static int Decode(string? cursor, int fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return fallback;
        }

        try
        {
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));

            return text.StartsWith("o", StringComparison.Ordinal) && int.TryParse(text[1..], out var offset) && offset >= 0
                ? offset
                : fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }
}