using System.Net;
using FluentAssertions;
using Inbox.Application.Common;
using Inbox.Application.Conversations;
using Inbox.Infrastructure.Conversations;
using Inbox.IntegrationTests.Infrastructure;

namespace Inbox.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class ConversationWorkflowTests(ApiFactory factory)
{
    [Fact]
    public async Task An_agent_takes_releases_and_takes_again()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Hana Farouk", "Who is on duty?");

        var taken = await ConversationScenarios.ActionAsync(session.Client, conversation.Id, "take");

        taken.AssignedAgentId.Should().Be(agent.Id);
        taken.Events.Should().ContainSingle(x => x.Type == "Taken");

        var released = await ConversationScenarios.ActionAsync(session.Client, conversation.Id, "release");

        released.AssignedAgentId.Should().BeNull();
        released.Events.Should().ContainSingle(x => x.Type == "Released");

        var retaken = await ConversationScenarios.ActionAsync(session.Client, conversation.Id, "take");

        retaken.AssignedAgentId.Should().Be(agent.Id);
    }

    [Fact]
    public async Task Taking_a_conversation_you_already_own_is_a_no_op()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Rami Fathi", "Hello");

        await ConversationScenarios.ActionAsync(session.Client, conversation.Id, "take");

        // The UI fires take on both the list row and the detail header, so a second call has to be
        // harmless rather than a 409.
        var again = await ConversationScenarios.ActionAsync(session.Client, conversation.Id, "take");

        again.AssignedAgentId.Should().Be(agent.Id);
        again.Events.Count(x => x.Type == "Taken").Should().Be(1);
    }

    [Fact]
    public async Task Two_agents_racing_for_one_conversation_produce_one_winner()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var first = await AgentScenarios.CreateAsync(admin.Client);
        var second = await AgentScenarios.CreateAsync(admin.Client);
        var firstSession = await ApiClient.LoginAsync(factory, first.Email, first.TemporaryPassword);
        var secondSession = await ApiClient.LoginAsync(factory, second.Email, second.TemporaryPassword);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Rania Zaki", "I need help now");

        // Both read the conversation as unassigned, then both try to claim it. RowVersion means the
        // loser's update affects zero rows, which surfaces as a 409 instead of a silent overwrite.
        var responses = await Task.WhenAll(
            firstSession.Client.PostAsync($"/api/conversations/{conversation.Id}/take"),
            secondSession.Client.PostAsync($"/api/conversations/{conversation.Id}/take"));

        var statuses = responses.Select(x => x.StatusCode).ToList();

        statuses.Should().Contain(HttpStatusCode.OK);
        statuses.Should().Contain(HttpStatusCode.Conflict);

        var loser = responses.First(x => x.StatusCode == HttpStatusCode.Conflict);
        (await ApiClient.ReadProblemAsync(loser)).ErrorCode.Should().Be(ConversationErrors.AlreadyTaken);

        var winner = responses.First(x => x.StatusCode == HttpStatusCode.OK);
        var stored = await firstSession.Client.ReadAsync<ConversationDetailResponse>(winner);

        stored.AssignedAgentId.Should().Be(first.Id == stored.AssignedAgentId ? first.Id : second.Id);

        // Exactly one take event, so the history does not claim two agents picked it up.
        stored.Events.Count(x => x.Type == "Taken").Should().Be(1);

        var reloaded = await ConversationScenarios.OpenAsync(admin.Client, conversation.Id);
        reloaded.AssignedAgentId.Should().Be(stored.AssignedAgentId);
    }

    [Fact]
    public async Task An_agent_cannot_release_a_conversation_owned_by_someone_else()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var owner = await AgentScenarios.CreateAsync(admin.Client);
        var other = await AgentScenarios.CreateAsync(admin.Client);
        var ownerSession = await ApiClient.LoginAsync(factory, owner.Email, owner.TemporaryPassword);
        var otherSession = await ApiClient.LoginAsync(factory, other.Email, other.TemporaryPassword);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Mona Saeed", "Hello");

        await ConversationScenarios.ActionAsync(ownerSession.Client, conversation.Id, "take");

        var release = await otherSession.Client.PostAsync($"/api/conversations/{conversation.Id}/release");

        release.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ApiClient.ReadProblemAsync(release)).ErrorCode.Should().Be(ConversationErrors.NotAssignedToYou);

        var stored = await ConversationScenarios.OpenAsync(ownerSession.Client, conversation.Id);

        stored.AssignedAgentId.Should().Be(owner.Id);
    }

    [Fact]
    public async Task An_admin_cannot_silently_steal_a_conversation_someone_else_owns()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var owner = await AgentScenarios.CreateAsync(admin.Client);
        var ownerSession = await ApiClient.LoginAsync(factory, owner.Email, owner.TemporaryPassword);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Karim Nabil", "Hello");

        await ConversationScenarios.ActionAsync(ownerSession.Client, conversation.Id, "take");

        var steal = await admin.Client.PostAsync($"/api/conversations/{conversation.Id}/take");

        // Even an admin goes through transfer, so the history never pretends nobody was working on it.
        steal.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.ReadProblemAsync(steal)).ErrorCode.Should().Be(ConversationErrors.AlreadyTaken);

        var reassigned = await ConversationScenarios.ActionAsync(
            admin.Client, conversation.Id, "transfer", new TransferConversationRequest(admin.Tokens.Agent.Id));

        reassigned.AssignedAgentId.Should().Be(admin.Tokens.Agent.Id);
    }

    [Fact]
    public async Task An_admin_can_take_an_unassigned_conversation()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Sami Derar", "Hello");

        var taken = await ConversationScenarios.ActionAsync(admin.Client, conversation.Id, "take");

        taken.AssignedAgentId.Should().Be(admin.Tokens.Agent.Id);
    }

    [Fact]
    public async Task A_transfer_moves_the_thread_to_another_agent()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var owner = await AgentScenarios.CreateAsync(admin.Client);
        var colleague = await AgentScenarios.CreateAsync(admin.Client);
        var ownerSession = await ApiClient.LoginAsync(factory, owner.Email, owner.TemporaryPassword);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Dina Aziz", "Please reassign me");

        await ConversationScenarios.ActionAsync(ownerSession.Client, conversation.Id, "take");

        var transferred = await ConversationScenarios.ActionAsync(
            ownerSession.Client, conversation.Id, "transfer", new TransferConversationRequest(colleague.Id));

        transferred.AssignedAgentId.Should().Be(colleague.Id);
        transferred.Events.Should().ContainSingle(x => x.Type == "Transferred"
            && x.ActorAgentId == owner.Id && x.TargetAgentId == colleague.Id);
    }

    [Fact]
    public async Task A_transfer_rejects_an_unknown_or_deactivated_agent()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var owner = await AgentScenarios.CreateAsync(admin.Client);
        var deactivated = await AgentScenarios.CreateAsync(admin.Client);
        var ownerSession = await ApiClient.LoginAsync(factory, owner.Email, owner.TemporaryPassword);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Amr Fouad", "Hello");

        await ConversationScenarios.ActionAsync(ownerSession.Client, conversation.Id, "take");

        var unknown = await ownerSession.Client.PostAsync(
            $"/api/conversations/{conversation.Id}/transfer",
            new TransferConversationRequest(Guid.NewGuid()));

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ApiClient.ReadProblemAsync(unknown)).ErrorCode.Should().Be(ConversationErrors.AgentNotFound);

        await AgentScenarios.DeactivateAsync(admin.Client, deactivated.Id);

        var inactive = await ownerSession.Client.PostAsync(
            $"/api/conversations/{conversation.Id}/transfer",
            new TransferConversationRequest(deactivated.Id));

        inactive.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(inactive)).ErrorCode.Should().Be(ConversationErrors.AgentInactive);
    }

    [Fact]
    public async Task Transferring_requires_the_conversation_to_be_taken_first()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var colleague = await AgentScenarios.CreateAsync(admin.Client);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Salma Idris", "Hello");

        var response = await admin.Client.PostAsync(
            $"/api/conversations/{conversation.Id}/transfer",
            new TransferConversationRequest(colleague.Id));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be(ConversationErrors.NotAssigned);
    }

    [Fact]
    public async Task Resolving_keeps_the_assignee_and_reopen_hands_the_thread_back()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var owner = await AgentScenarios.CreateAsync(admin.Client);
        var ownerSession = await ApiClient.LoginAsync(factory, owner.Email, owner.TemporaryPassword);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Tarek Jaber", "Thanks, bye");

        await ConversationScenarios.ActionAsync(ownerSession.Client, conversation.Id, "take");
        var resolved = await ConversationScenarios.ActionAsync(ownerSession.Client, conversation.Id, "resolve");

        resolved.Status.Should().Be("Resolved");

        // Keeping the assignee is deliberate: an agent who returns to the thread finds it as it was.
        resolved.AssignedAgentId.Should().Be(owner.Id);

        var reopened = await ConversationScenarios.ActionAsync(ownerSession.Client, conversation.Id, "reopen");

        reopened.Status.Should().Be("Open");
        reopened.AssignedAgentId.Should().Be(owner.Id);

        var mine = await ConversationScenarios.ListAsync(ownerSession.Client, "filter=mine");

        mine.Items.Should().Contain(x => x.Id == conversation.Id);
    }

    [Fact]
    public async Task Resolving_twice_is_rejected_and_reopening_an_open_conversation_too()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Lina Hamdy", "Hello");

        var reopenFirst = await admin.Client.PostAsync($"/api/conversations/{conversation.Id}/reopen");

        reopenFirst.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.ReadProblemAsync(reopenFirst)).ErrorCode.Should().Be(ConversationErrors.NotResolved);

        await ConversationScenarios.ActionAsync(admin.Client, conversation.Id, "resolve");

        var resolveAgain = await admin.Client.PostAsync($"/api/conversations/{conversation.Id}/resolve");

        resolveAgain.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.ReadProblemAsync(resolveAgain)).ErrorCode.Should().Be(ConversationErrors.AlreadyResolved);
    }

    [Fact]
    public async Task Reopening_releases_a_conversation_whose_owner_has_left()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var owner = await AgentScenarios.CreateAsync(admin.Client);
        var ownerSession = await ApiClient.LoginAsync(factory, owner.Email, owner.TemporaryPassword);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Omar Shafik", "Hello");

        await ConversationScenarios.ActionAsync(ownerSession.Client, conversation.Id, "take");
        await ConversationScenarios.ActionAsync(ownerSession.Client, conversation.Id, "resolve");
        await AgentScenarios.DeactivateAsync(admin.Client, owner.Id);

        var reopened = await ConversationScenarios.ActionAsync(admin.Client, conversation.Id, "reopen");

        // A thread owned by a deactivated agent has to go back to the pool, or nobody would pick it up.
        reopened.Status.Should().Be("Open");
        reopened.AssignedAgentId.Should().BeNull();
    }

    [Fact]
    public async Task Marking_read_clears_the_unread_count()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var contactId = ConversationScenarios.NewExternalContactId();

        await ConversationScenarios.DeliverAsync(admin.Client, "Zaid Khalil", "One", externalContactId: contactId);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Zaid Khalil", "Two", externalContactId: contactId);

        conversation.UnreadCount.Should().Be(2);

        (await admin.Client.PostAsync($"/api/conversations/{conversation.Id}/read")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        (await ConversationScenarios.OpenAsync(admin.Client, conversation.Id)).UnreadCount.Should().Be(0);
    }

    [Fact]
    public async Task The_list_filters_by_owner_status_and_search()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);

        var contactId = ConversationScenarios.NewExternalContactId();
        var (_, taken) = await ConversationScenarios.DeliverAsync(admin.Client, "Nermin Kaya", "Mine", externalContactId: contactId);
        var (_, untouched) = await ConversationScenarios.DeliverAsync(admin.Client, "Emre Yilmaz", "Unassigned");

        await ConversationScenarios.ActionAsync(session.Client, taken.Id, "take");

        var mine = await ConversationScenarios.ListAsync(session.Client, "filter=mine");
        mine.Items.Should().Contain(x => x.Id == taken.Id).And.NotContain(x => x.Id == untouched.Id);

        var unassigned = await ConversationScenarios.ListAsync(admin.Client, "filter=unassigned");
        unassigned.Items.Should().Contain(x => x.Id == untouched.Id).And.NotContain(x => x.Id == taken.Id);

        var bySearch = await ConversationScenarios.ListAsync(admin.Client, "filter=all&search=Nermin Kaya");
        bySearch.Items.Should().ContainSingle(x => x.Id == taken.Id);

        await ConversationScenarios.ActionAsync(admin.Client, untouched.Id, "resolve");

        var resolved = await ConversationScenarios.ListAsync(admin.Client, "filter=resolved");
        resolved.Items.Should().Contain(x => x.Id == untouched.Id);

        var all = await ConversationScenarios.ListAsync(admin.Client);
        all.Items.Should().NotContain(x => x.Id == untouched.Id);
    }

    [Fact]
    public async Task An_unknown_filter_is_a_validation_error()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);

        var response = await admin.Client.Http.GetAsync("/api/conversations?filter=whatever");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be("conversation.invalid_filter");
    }

    [Fact]
    public async Task Messages_are_paged_newest_first_with_a_working_cursor()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var contactId = ConversationScenarios.NewExternalContactId();
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Fouad Zaki", "One", externalContactId: contactId);

        for (var i = 2; i <= 5; i++)
        {
            await ConversationScenarios.DeliverAsync(admin.Client, "Fouad Zaki", $"Message {i}", externalContactId: contactId);
        }

        var firstPage = await admin.Client.ReadAsync<PagedResult<ConversationMessageResponse>>(
            await admin.Client.Http.GetAsync($"/api/conversations/{conversation.Id}/messages?pageSize=2"));

        firstPage.Items.Should().HaveCount(2);
        firstPage.Items[0].Text.Should().Be("Message 5");
        firstPage.NextCursor.Should().NotBeNullOrWhiteSpace();

        // The cursor is the id of the last item on the page.
        var secondPage = await admin.Client.ReadAsync<PagedResult<ConversationMessageResponse>>(
            await admin.Client.Http.GetAsync(
                $"/api/conversations/{conversation.Id}/messages?pageSize=2&before={firstPage.NextCursor}"));

        secondPage.Items.Should().HaveCount(2);
        secondPage.Items[0].Text.Should().Be("Message 3");
        secondPage.Items.Select(x => x.Text).Should().NotIntersectWith(firstPage.Items.Select(x => x.Text));

        var all = await ConversationScenarios.MessagesAsync(admin.Client, conversation.Id);

        all.Items.Select(x => x.Text).Should().Equal("Message 5", "Message 4", "Message 3", "Message 2", "One");
    }

    [Fact]
    public async Task An_unknown_conversation_is_a_not_found()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var missing = Guid.NewGuid();

        (await admin.Client.Http.GetAsync($"/api/conversations/{missing}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await admin.Client.PostAsync($"/api/conversations/{missing}/take")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await admin.Client.PostAsync($"/api/conversations/{missing}/resolve")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reading_a_conversation_requires_a_token()
    {
        var anonymous = ApiClient.Anonymous(factory);

        (await anonymous.Http.GetAsync("/api/conversations")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        (await anonymous.PostAsync($"/api/conversations/{Guid.NewGuid()}/take")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }
}