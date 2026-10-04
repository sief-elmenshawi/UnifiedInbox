using System.Security.Cryptography;

namespace Inbox.Application.Common;

/// <summary>
/// Generates the temporary passwords that admins hand out when creating or resetting an agent.
/// The output always satisfies the configured Identity password policy, because a password that
/// fails validation would leave the new agent unable to log in.
/// </summary>
public static class TemporaryPassword
{
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Digits = "23456789";
    private const string Symbols = "!@#$%*-_";
    private const string All = Lower + Upper + Digits + Symbols;

    public const int DefaultLength = 16;

    public static string Generate(int length = DefaultLength)
    {
        if (length < 12)
        {
            length = 12;
        }

        var characters = new List<char>(length)
        {
            Pick(Lower),
            Pick(Upper),
            Pick(Digits),
            Pick(Symbols)
        };

        while (characters.Count < length)
        {
            characters.Add(Pick(All));
        }

        // Fisher-Yates with a cryptographic source, so the guaranteed characters are not
        // always in the first four positions.
        Shuffle(characters);

        return new string([.. characters]);
    }

    private static char Pick(string source) => source[RandomNumberGenerator.GetInt32(source.Length)];

    private static void Shuffle(List<char> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}