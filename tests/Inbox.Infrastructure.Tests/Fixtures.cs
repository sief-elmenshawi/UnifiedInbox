namespace Inbox.Infrastructure.Tests;

/// <summary>
/// Reads the recorded provider payloads from <c>tests/Fixtures</c>. They are copied next to the test
/// binaries by the project file, so a failing test can be reproduced by replaying the same bytes.
/// </summary>
internal static class Fixtures
{
    public static string Read(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

        return File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException(
                $"Fixture '{name}' was not copied to the output directory.", path);
    }
}