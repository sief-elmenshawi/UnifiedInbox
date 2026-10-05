namespace Inbox.IntegrationTests.Infrastructure;

/// <summary>
/// The API as it is served for real, against the database container <see cref="ApiFactory"/> already
/// provides (the connection string is in the process environment). Used to prove that the dev-only
/// endpoints and the dev-only channel adapter do not exist outside Development.
/// </summary>
public sealed class ProductionApiFactory() : AuxiliaryApiFactory("Production");
