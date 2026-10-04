using Inbox.Application.Channels;
using Inbox.Application.Inbound;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Inbox.Infrastructure.Inbound;

/// <summary>
/// Drains the inbound queue on a background thread, so the webhook endpoint can answer Meta
/// immediately. One message at a time and per-message error handling: a poison message must not stop
/// the queue.
/// </summary>
public sealed class InboundProcessorWorker(
    IInboundMessageQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<InboundProcessorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Inbound processor started.");

        await foreach (var message in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                // The processor needs a DbContext per message, so it is resolved inside a scope
                // instead of being injected here - a hosted service lives for the whole app.
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IInboundProcessor>();

                var result = await processor.ProcessAsync(message, stoppingToken);

                if (result.IsFailure)
                {
                    logger.LogWarning(
                        "Inbound event {ExternalMessageId} was rejected: {Code} {Message}",
                        message.ExternalMessageId,
                        result.Error!.Code,
                        result.Error.Message);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Never let one bad event kill the worker.
                logger.LogError(
                    exception,
                    "Inbound event {ExternalMessageId} could not be processed.",
                    message.ExternalMessageId);
            }
        }
    }
}