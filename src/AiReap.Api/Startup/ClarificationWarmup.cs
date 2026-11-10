using System.Diagnostics;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Persistence;

namespace AiReap.Api.Startup;

// The first page opened after an API restart used to wait while every clarification question of a large
// source was linked to it again (ClarificationVisibility). Shortly after start this links them once, in
// the background, so that wait is not on a person's request. Read-only: the database is only read and
// nothing is persisted; requests that arrive meanwhile are served normally and share the work.
//
// Settings (section ClarificationVisibility), all optional:
//   WarmOnStartup        true   - false turns it off
//   WarmStartDelaySeconds 5     - wait after start, so startup and the first requests are not slowed
//   WarmMaxSources       200    - at most this many sources, most recently changed first
//   WarmMaxSeconds       300    - no new source is started after this long
// It never fails the application: any error is logged and the first read does the work instead.
public sealed class ClarificationWarmup : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClarificationWarmup> _logger;

    public ClarificationWarmup(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ClarificationWarmup> logger)
    {
        _scopes = scopes;
        _configuration = configuration;
        _logger = logger;
    }

    public sealed record Settings(bool Enabled, TimeSpan StartDelay, int MaxSources, TimeSpan Budget);

    public static Settings Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("ClarificationVisibility");
        return new Settings(
            section.GetValue("WarmOnStartup", true),
            TimeSpan.FromSeconds(Math.Clamp(section.GetValue("WarmStartDelaySeconds", 5), 0, 3600)),
            Math.Max(0, section.GetValue("WarmMaxSources", 200)),
            TimeSpan.FromSeconds(Math.Clamp(section.GetValue("WarmMaxSeconds", 300), 1, 86_400)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = Read(_configuration);
        if (!settings.Enabled || settings.MaxSources == 0)
        {
            return;
        }

        try
        {
            // The first await: startup continues while this waits.
            await Task.Delay(settings.StartDelay, stoppingToken);
            var watch = Stopwatch.StartNew();
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAiReapDbContext>();
            var count = await ClarificationVisibility.WarmAsync(db, stoppingToken, settings.MaxSources, settings.Budget);
            _logger.LogInformation("Clarification questions prepared for reading: {Count} in {Elapsed} ms.", count, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Only a head start: the first read does the same work if this could not.
            _logger.LogWarning(ex, "Preparing clarification questions at startup failed; they are prepared on first read instead.");
        }
    }
}
