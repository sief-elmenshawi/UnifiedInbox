using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Serilog;

namespace Inbox.IntegrationTests.Infrastructure;

/// <summary>
/// A second host of the same API - a different environment, or different settings - against the
/// database container <see cref="ApiFactory"/> already provides.
/// <para>
/// Settings arrive as environment variables, which is how the suite configures every host, and those
/// are process-wide rather than per host. The log file has to be redirected for the same reason: two
/// Serilog file sinks pointed at one path share a single stream, so when this host is disposed the file
/// the <em>main</em> host is still logging to is closed underneath it, and everything logged afterwards
/// is silently lost. Each auxiliary host therefore writes to a file of its own and puts back what it
/// found when it goes away.
/// </para>
/// </summary>
public abstract class AuxiliaryApiFactory : WebApplicationFactory<Program>
{
    private const string LogPathVariable = "Serilog__WriteTo__1__Args__path";

    private readonly string _environment;
    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly Dictionary<string, string?> _found = new(StringComparer.Ordinal);
    private readonly ILogger _loggerBefore = Log.Logger;

    protected AuxiliaryApiFactory(string environment, IReadOnlyDictionary<string, string?>? settings = null)
    {
        _environment = environment;
        _settings = settings ?? new Dictionary<string, string?>();

        LogPath = Path.Combine(AppContext.BaseDirectory, $"inbox-tests-aux-{Guid.NewGuid():N}.log");

        Set(LogPathVariable, LogPath);

        foreach (var setting in _settings)
        {
            Set(setting.Key, setting.Value);
        }
    }

    /// <summary>Where this host writes its log, which is not the suite's log file.</summary>
    public string LogPath { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(_environment);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        // Building this host replaced the static Log.Logger, and UseSerilogRequestLogging() reads that
        // static rather than an injected logger. Left alone, the main host would keep writing its request
        // lines - every line, hub requests included - into this host's logger after this host is gone.
        // Handing the previous logger back is what keeps the suite's log complete.
        Log.Logger = _loggerBefore;

        foreach (var (name, value) in _found)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private void Set(string name, string? value)
    {
        _found.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }
}
