using FluentAssertions;
using Inbox.Domain.Entities;

namespace Inbox.Domain.Tests.Entities;

public class ConversationTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Conversation NewConversation() => Conversation.CreateOpen(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Now);

    [Fact]
    public void CreateOpen_StartsUnassignedAndOpen()
    {
        var conversation = NewConversation();

        conversation.Status.Should().Be(Domain.Enums.ConversationStatus.Open);
        conversation.AssignedAgentId.Should().BeNull();
        conversation.UnreadCount.Should().Be(0);
        conversation.LastMessageAtUtc.Should().Be(Now);
        conversation.RowVersion.Should().BeNull();
    }

    [Fact]
    public void RecordInbound_OpensTheReplyWindowForExactly24Hours()
    {
        var conversation = NewConversation();

        conversation.RecordInbound("hello", Now);

        conversation.LastInboundAtUtc.Should().Be(Now);
        conversation.WindowExpiresAtUtc.Should().Be(Now.AddHours(24));
        conversation.LastMessagePreview.Should().Be("hello");
        conversation.UnreadCount.Should().Be(1);
    }

    [Fact]
    public void ReplyWindow_IsOpenBeforeExpiry_AndClosedAfter()
    {
        var conversation = NewConversation();
        conversation.RecordInbound("hello", Now);

        conversation.IsReplyWindowOpen(Now.AddHours(23)).Should().BeTrue();
        conversation.IsReplyWindowOpen(Now.AddHours(24)).Should().BeFalse();
        conversation.IsReplyWindowOpen(Now.AddHours(25)).Should().BeFalse();
    }

    [Fact]
    public void ReplyWindow_IsClosedWhenTheCustomerNeverWrote()
    {
        var conversation = NewConversation();

        conversation.WindowExpiresAtUtc.Should().BeNull();
        conversation.IsReplyWindowOpen(Now).Should().BeFalse();
    }

    [Fact]
    public void RecordInbound_ExtendsTheWindowFromTheLatestMessage()
    {
        var conversation = NewConversation();
        conversation.RecordInbound("first", Now);

        conversation.RecordInbound("second", Now.AddHours(2));

        conversation.WindowExpiresAtUtc.Should().Be(Now.AddHours(26));
        conversation.UnreadCount.Should().Be(2);
    }

    [Fact]
    public void RecordOutbound_DoesNotExtendTheReplyWindow()
    {
        var conversation = NewConversation();
        conversation.RecordInbound("first", Now);

        conversation.RecordOutbound("our reply", Now.AddHours(1));

        conversation.WindowExpiresAtUtc.Should().Be(Now.AddHours(24));
        conversation.UnreadCount.Should().Be(1);
        conversation.LastMessagePreview.Should().Be("our reply");
    }

    [Fact]
    public void Preview_IsTruncatedAndNeverNullForEmptyText()
    {
        var conversation = NewConversation();

        conversation.RecordInbound(new string('x', 500), Now);
        conversation.LastMessagePreview.Should().HaveLength(140);

        conversation.RecordOutbound("   ", Now.AddMinutes(1));
        conversation.LastMessagePreview.Should().HaveLength(140);
    }

    [Fact]
    public void MarkRead_ResetsUnreadCount()
    {
        var conversation = NewConversation();
        conversation.RecordInbound("hi", Now);

        conversation.MarkRead();

        conversation.UnreadCount.Should().Be(0);
    }
}