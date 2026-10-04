namespace Inbox.IntegrationTests.Infrastructure;

/// <summary>
/// Every test shares one container and one API host. Sharing means the suite pays the SQL Server
/// startup cost once, and it keeps the tests serialised, which matters because Identity's lockout
/// counters are per account.
/// </summary>
[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "integration";
}