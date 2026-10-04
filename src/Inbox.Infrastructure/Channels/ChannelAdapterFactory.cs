using Inbox.Application.Channels;
using Inbox.Domain.Enums;

namespace Inbox.Infrastructure.Channels;

public sealed class ChannelAdapterFactory(IEnumerable<IChannelAdapter> adapters) : IChannelAdapterFactory
{
    private readonly Dictionary<ChannelType, IChannelAdapter> _adapters =
        adapters.ToDictionary(x => x.Type);

    public bool Supports(ChannelType type) => _adapters.ContainsKey(type);

    public IChannelAdapter Get(ChannelType type) =>
        _adapters.TryGetValue(type, out var adapter)
            ? adapter
            : throw new InvalidOperationException($"No adapter is registered for channel type '{type}'.");
}