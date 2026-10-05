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

                // One queue carries both halves of a provider conversation: a new message from a
                // customer, and a status report about something we sent. Same lifecycle, different
                // work, and the status one is never fatal - an unknown id is expected sometimes.
                var failure = message.Kind == InboundEventKind.StatusUpdate
                    ? (await processor.ProcessStatusAsync(message, stoppingToken)).Error
                    : (await processor.ProcessAsync(message, stoppingToken)).Error;

                if (failure is not null)
                {
                    logger.LogWarning(
                        "Inbound event {ExternalMessageId} was rejected: {Code} {Message}",
                        message.ExternalMessageId,
                        failure.Code,
                        failure.Message);
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