using System.Net;
using System.Text;
using FluentAssertions;
using Inbox.IntegrationTests.Infrastructure;

namespace Inbox.IntegrationTests;

/// <summary>
/// The wire ceiling on a request body, per endpoint. One number cannot be right: a JSON call is
/// small, the webhook is anonymous, the file send is bytes. These tests prove the profiles are
/// applied by the middleware - before the signature check, before authentication, before the
/// binder - which under TestServer is exactly what a caller with a declared Content-Length hits,
/// since the test host enforces neither Kestrel's own limit nor the feature.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class RequestSizeLimitTests
{
    private readonly ApiFactory factory;

    public RequestSizeLimitTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task A_webhook_body_over_the_webhook_ceiling_is_refused_before_the_controller_reads_it()
    {
        var anonymous = ApiClient.Anonymous(factory);

        // 2 MB of body with a declared length: over the webhook's own ceiling, and unsigned - so
        // the controller would answer 401 were it ever reached. A 413 instead is proof the size
        // check refused the request first, before any signature work or database call.
        var response = await anonymous.Http.PostAsync(
            "/api/webhooks/whatsapp",
            new StringContent(new string('a', 2 * 1024 * 1024), Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        var problem = await ApiClient.ReadProblemAsync(response);
        problem.ErrorCode.Should().Be("request.too_large");
        problem.Detail.Should().Contain("1 MB");
    }

    [Fact]
    public async Task A_file_over_the_default_ceiling_but_within_the_media_ceiling_is_accepted()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        // 3 MB with a real PNG signature: over the default ceiling that refuses a JSON body of the
        // same size, under the 5 MB an outbound image is allowed - so what accepts it is the send
        // route's own profile, not the default.
        var png = new byte[3 * 1024 * 1024];
        png[0] = 0x89;
        png[1] = 0x50;
        png[2] = 0x4E;
        png[3] = 0x47;
        png[4] = 0x0D;
        png[5] = 0x0A;
        png[6] = 0x1A;
        png[7] = 0x0A;

        var response = await admin.PostMultipartAsync(
            $"/api/conversations/{conversation.Id}/messages",
            "screenshot.png",
            png,
            "image/png");

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var upload = channel.Uploads.Should().ContainSingle().Subject;
        upload.Content.Should().Equal(png);
    }

    [Fact]
    public async Task A_json_body_over_the_default_ceiling_is_refused()
    {
        var admin = (await ApiClient.LoginAsAdminAsync(factory)).Client;
        var (_, conversation) = await WhatsAppScenarios.DeliverAsync(factory);

        // 3 MB of JSON against an endpoint whose profile is the default. Refused before the
        // authentication middleware, which is why a valid token makes no difference to the answer.
        var response = await admin.Http.PostAsync(
            $"/api/conversations/{conversation.Id}/messages",
            new StringContent(
                $"{{\"text\":\"{new string('a', 3 * 1024 * 1024)}\"}}",
                Encoding.UTF8,
                "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        var problem = await ApiClient.ReadProblemAsync(response);
        problem.ErrorCode.Should().Be("request.too_large");
        problem.Detail.Should().Contain("2 MB");
    }
}
