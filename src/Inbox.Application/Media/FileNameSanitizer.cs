namespace Inbox.Application.Media;

/// <summary>
/// Reduces a file name that came from outside the system to something safe to show a user and safe to
/// put in a <c>Content-Disposition</c> header.
/// </summary>
/// <remarks>
/// The name is never used to build the path on disk - storage generates its own name from a GUID - so
/// this is about display and header injection, not about traversal. It still strips directory
/// components, because a stored name of <c>..\..\web.config</c> is both misleading to an agent and a
/// header-splitting risk if it is ever echoed back.
/// </remarks>
public static class FileNameSanitizer
{
    /// <summary>Matches the column width so a name that survives is always storable.</summary>
    public const int MaxLength = 256;

    /// <summary>Characters Windows refuses in a file name, plus the separators.</summary>
    private static readonly char[] Forbidden = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly char[] ControlCharacters =
        [.. Enumerable.Range(0, 32).Select(index => (char)index)];

    /// <summary>
    /// Names Windows reserves for its own devices. A file called <c>con.txt</c> cannot be created, and
    /// sanitising it to something a person can read beats returning an empty name.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Produces a name safe to store and display. Never returns null, empty, or a bare extension.
    /// </summary>
    /// <param name="fileName">The name as supplied by Meta or by the uploading agent. May be null.</param>
    /// <param name="fallbackBaseName">
    /// Used when nothing usable survives sanitising - <c>attachment</c> for an unnamed inbound file.
    /// </param>
    public static string Sanitize(string? fileName, string fallbackBaseName = "attachment")
    {
        var candidate = (fileName ?? string.Empty).Trim();

        // Take the last segment: a name that arrived with a path in it is trying to describe a location,
        // and only the file part is ever interesting.
        var separator = candidate.LastIndexOfAny(['/', '\\']);
        if (separator >= 0)
        {
            candidate = candidate[(separator + 1)..];
        }

        candidate = new string(candidate.Where(character => !Forbidden.Contains(character) && !ControlCharacters.Contains(character)).ToArray()).Trim();

        // A leading dot turns the name into a hidden file on Unix, and ".", ".." and "" are not names.
        candidate = candidate.TrimStart('.').Trim();

        if (candidate.Length == 0)
        {
            candidate = fallbackBaseName;
        }

        candidate = TruncateKeepingExtension(candidate);

        return IsReserved(candidate) ? $"{fallbackBaseName}{Path.GetExtension(candidate)}" : candidate;
    }

    /// <summary>
    /// Trims the base name so the result fits <see cref="MaxLength"/> while keeping the extension,
    /// because an agent recognises <c>invoice.pdf</c> and not <c>invoice-very-long-name-pdf</c>.
    /// </summary>
    private static string TruncateKeepingExtension(string fileName)
    {
        if (fileName.Length <= MaxLength)
        {
            return fileName;
        }

        var extension = Path.GetExtension(fileName);
        var allowance = Math.Max(1, MaxLength - extension.Length);

        return fileName[..allowance] + extension;
    }

    private static bool IsReserved(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);

        return ReservedNames.Contains(stem);
    }
}