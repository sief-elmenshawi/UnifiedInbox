namespace Inbox.Infrastructure.Channels;

/// <summary>
/// Identifiers of the local test channel. The seeder creates the row and the dev simulator resolves
/// it by external account id, so tests and the simulator do not have to look it up first.
/// </summary>
public static class TestChannel
{
    public const string ExternalAccountId = "test-account";

    public const string DisplayName = "Test channel";
}