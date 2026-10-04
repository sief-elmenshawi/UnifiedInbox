using FluentAssertions;
using Inbox.Domain.Entities;

namespace Inbox.Domain.Tests.Entities;

public class MessageTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CreateInbound_IsCustomerDirectionAndAlreadyDelivered()
    {
        var message = Message.CreateInbound(Guid.NewGuid(), Guid.NewGuid(), "hi", "wamid.1", Domain.Enums.MessageType.Text, Now);

        message.Direction.Should().Be(Domain.Enums.MessageDirection.Inbound);
        message.SenderType.Should().Be(Domain.Enums.MessageSenderType.Customer);
        message.SenderAgentId.Should().BeNull();
        message.DeliveryStatus.Should().Be(Domain.Enums.DeliveryStatus.Delivered);
        message.ExternalMessageId.Should().Be("wamid.1");
    }

    [Fact]
    public void CreateOutbound_IsPendingAndCarriesTheAgent()
    {
        var agentId = Guid.NewGuid();

        var message = Message.CreateOutbound(Guid.NewGuid(), Guid.NewGuid(), agentId, "hello", Domain.Enums.MessageType.Text, Now);

        message.Direction.Should().Be(Domain.Enums.MessageDirection.Outbound);
        message.SenderType.Should().Be(Domain.Enums.MessageSenderType.Agent);
        message.SenderAgentId.Should().Be(agentId);
        message.DeliveryStatus.Should().Be(Domain.Enums.DeliveryStatus.Pending);
        message.ExternalMessageId.Should().BeNull();
    }
}