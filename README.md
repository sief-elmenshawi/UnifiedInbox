# Unified Inbox — API

Internal shared-inbox tool for a single company: WhatsApp, Instagram and Facebook conversations from one
dashboard. Backend is ASP.NET Core 8; the Angular dashboard is added in a later phase.

Full requirements and design decisions live in [`docs/unified-inbox-spec.md`](docs/unified-inbox-spec.md).

## Prerequisites

- .NET 8 SDK (`dotnet --version`)
- SQL Server — either LocalDB (default config) or any SQL Server 2022 instance
- Docker Desktop — only needed to run the integration tests (they start a SQL Server container)

## First-time setup

```powershell
Copy-Item src\Inbox.Api\appsettings.Development.json.example src\Inbox.Api\appsettings.Development.json
```

`appsettings.Development.json` is git-ignored, so edit your copy freely. Real secrets belong in
user-secrets instead of the file:

```powershell
dotnet user-secrets --project src/Inbox.Api set "Jwt:SigningKey" "<at least 32 characters>"
dotnet user-secrets --project src/Inbox.Api list
```

## Running it

```powershell
dotnet run --project src/Inbox.Api
```

Then open <http://localhost:5110/swagger>. The seeded admin is `admin@local.test` with the
`Seed:AdminPassword` from your appsettings copy — change it after the first login.

> Run it with `dotnet run` or F5, **not** by starting `bin\Debug\net8.0\Inbox.Api.exe` directly.
> `appsettings.Development.json` and user-secrets are only loaded when `ASPNETCORE_ENVIRONMENT=Development`,
> which the launch profile sets for you. If you must start the exe, set that variable yourself.

The API creates the database schema and seeds the first admin on startup in Development only. In
Production, migrations are a deployment step:

```powershell
dotnet ef migrations script --idempotent -o migrate.sql --project src/Inbox.Infrastructure --startup-project src/Inbox.Api
```

## Trying it without Meta

There is a fake WhatsApp channel (`ChannelType.Test`, seeded automatically) so the whole inbound and
reply path can be exercised locally. Inject a customer message as the seeded admin — this endpoint
exists **only** when the environment is Development:

```powershell
$token = (Invoke-RestMethod -Uri http://localhost:5110/api/auth/login -Method Post `
    -ContentType application/json `
    -Body '{"email":"admin@local.test","password":"<Seed:AdminPassword>"}').accessToken

Invoke-RestMethod -Uri http://localhost:5110/api/dev/simulate-inbound -Method Post `
    -Headers @{ Authorization = "Bearer $token" } -ContentType application/json `
    -Body '{"externalContactId":"wa-123","contactDisplayName":"Nadia","text":"I need a quote"}'

Invoke-RestMethod -Uri "http://localhost:5110/api/conversations?filter=all" `
    -Headers @{ Authorization = "Bearer $token" }
```

The endpoint returns `202 Accepted` as soon as the message is queued; the background worker stores it
a moment later. Text starting with `/fail` makes an outbound reply fail on purpose, so the failed
delivery path can be seen in the message history.

Main endpoints (`Authorization: Bearer <access token>`):

| Method | Path | Notes |
| --- | --- | --- |
| `GET` | `/api/conversations?filter=all\|unassigned\|mine\|resolved` | Agent list, cursor paginated, `search` matches name and preview. |
| `GET` | `/api/conversations/{id}` | Conversation plus its event history. |
| `GET` | `/api/conversations/{id}/messages?pageSize=50&before={messageId}` | Newest first. |
| `POST` | `/api/conversations/{id}/take\|release\|transfer\|resolve\|reopen\|read` | `transfer` body: `{"targetAgentId":"…"}`. |
| `POST` | `/api/conversations/{id}/messages` | Body `{"text":"…"}`; only inside the 24-hour reply window. |
| `POST` | `/api/conversations/{id}/template-messages` | Body `{"name":"follow_up_quote","languageCode":"en_US","bodyParameters":["Amir"]}`; allowed even when the 24-hour window is closed. |
| `GET` | `/api/channels` | Admin only. Secrets are never returned, only whether they are set. |
| `POST` | `/api/channels` | Admin only. Body `{"type":"WhatsApp","displayName":"…","externalAccountId":"…","accessToken":"…"}`. |
| `PUT` | `/api/channels/{id}` | Admin only. Omit a secret to keep the stored one. |
| `DELETE` | `/api/channels/{id}` | Admin only. Disconnects the channel; nothing is deleted and history stays readable. |
| `POST` | `/api/dev/simulate-inbound` | Development only, admin only. |

## WhatsApp (Cloud API)

Two settings decide whether sending is possible at all:

| Setting | Meaning |
| --- | --- |
| `Meta:GraphApiVersion` | The Graph version segment, e.g. `v21.0`. Without it a send is refused rather than guessed at. |
| `Meta:GraphBaseUrl` | Defaults to `https://graph.facebook.com`; point it somewhere else only for a proxy. |

A WhatsApp channel is then connected as an admin, and the channel row holds the four secrets Meta
needs. `externalAccountId` is the **phone number id** from the Meta dashboard, not the phone number:

```powershell
Invoke-RestMethod -Uri http://localhost:5110/api/channels -Method Post `
    -Headers @{ Authorization = "Bearer $token" } -ContentType application/json `
    -Body '{"type":"WhatsApp","displayName":"Support line","externalAccountId":"102290129340398","accessToken":"EAA…","appSecret":"…","webhookVerifyToken":"choose-anything"}'
```

Secrets are stored encrypted and never come back out of the API; the response only says whether each
one is set.

### The callback URL

Meta has to reach the API from the internet, so a tunnel is needed in local development - ngrok or
Cloudflare Tunnel both work:

```powershell
ngrok http 5110
# then set the callback to https://<subdomain>.ngrok-free.app/api/webhooks/whatsapp
```

Register the callback once with the same URL for the "messages" field. Meta then verifies it and
sends two kinds of payload to it.

**Verification** is a single `GET`; the challenge is echoed back only when the mode is `subscribe` and
the verify token matches a connected channel:

```
GET /api/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=…&hub.challenge=1158201444
```

**Deliveries** are `POST`s. The endpoint is anonymous by necessity, so the request is authenticated
by Meta's own signature instead: `X-Hub-Signature-256` is `sha256=` plus the HMAC-SHA256 of the raw
body keyed with the channel's app secret. The body is read as raw text and never re-serialised, because
that would change the bytes and break the check. A payload that fails verification is refused with
`401`; one for an unknown or disconnected channel is acknowledged with `200` and dropped, because
retrying could not make it attributable.

Responses are `200` as soon as the events are queued. Meta retries anything slower, and a retry storm
is worse than the work.

### What the payload becomes

An inbound delivery creates or updates a conversation, stores the message, and pushes
`MessageReceived` to the agents watching the thread. Media is recorded by type with its caption so the
thread stays readable; the file itself is downloaded in a later phase.

A status delivery reports on a message we sent, so it is matched by channel **and** the provider's
message id, and only ever moves a message forward:

| Provider status | Stored as |
| --- | --- |
| `sent` | `Sent` |
| `delivered` | `Delivered` |
| `read` | `Read` |
| `failed` | `Failed`, with the reason Meta gave |

Meta re-sends and re-orders these, so a `delivered` that arrives after a `failed` is ignored, and a
repeat changes nothing. Each accepted change is pushed as `MessageStatusUpdated` to the thread viewers.

### Templates

Free-form text is only accepted inside the customer's 24-hour window. An approved template is the only
message that goes through after it, which is why it has its own endpoint and ignores the window:

```
POST /api/conversations/{id}/template-messages
{"name":"follow_up_quote","languageCode":"en_US","bodyParameters":["Amir","245 USD"]}
```

`bodyParameters` are positional and map to the `{{1}}` placeholders in the approved template body. The
history stores what was sent so an agent reading the thread later sees the words.

### Talking to WhatsApp without an account

The integration tests never reach Meta. `StubGraphHandler` replaces the graph host with recorded
payloads and `tests/Fixtures/` holds real webhook bodies, so the signature check, the parser and the
status handling are exercised against the bytes Meta actually sends - with a real number, token and
Business Verification left to the operator.

## Live updates

The dashboard does not poll: it holds one SignalR connection and the API pushes state changes to it.

```
GET /hubs/inbox?access_token=<access token>
```

A WebSocket handshake cannot carry an `Authorization` header, so the token goes in the query string -
and only for this route. The client sends three methods:

| Method | Payload | Meaning |
| --- | --- | --- |
| `OpenConversation` | `conversationId` | I am looking at this thread; start sending me its messages. |
| `CloseConversation` | `conversationId` | I left it; stop sending thread events. |
| `Typing` | `conversationId` | I am typing; the other viewers see a typing indicator. |

| Event | Sent to | Fired when |
| --- | --- | --- |
| `ConversationCreated` | workspace | A first inbound message created the thread. |
| `ConversationUpdated` | workspace | Owner, status or unread count changed. |
| `ConversationAssigned` | workspace | The thread was taken, transferred or released. |
| `MessageReceived` | thread viewers | A message was stored, inbound or outbound. |
| `MessageStatusUpdated` | thread viewers | An outbound message changed delivery status. |
| `AgentViewing` / `AgentLeft` | thread viewers | Someone opened or closed the thread. |
| `AgentTyping` | other thread viewers | Someone is typing. |
| `AgentPresenceChanged` | workspace | Someone went online, away or offline. |

Workspace events reach every signed-in agent of that workspace. Thread events reach only the
connections that have that thread open, so an agent who never opened it is not interrupted by a
conversation that is not on their screen. State is held in memory per API process, which is enough
for the internal-tools scale this is built for; more than one instance would need a backplane.

## Tests

```powershell
dotnet test UnifiedInbox.sln                                        # everything
dotnet test tests\Inbox.Domain.Tests                                # fast, no Docker
dotnet test UnifiedInbox.sln --filter "FullyQualifiedName~AuthFlowTests"
dotnet test UnifiedInbox.sln --filter "FullyQualifiedName~LiveUpdateTests"   # hub events
dotnet test UnifiedInbox.sln --filter "FullyQualifiedName~WhatsAppWebhookTests"
```

The integration tests run the real API against a throwaway SQL Server container (Testcontainers), so
they need Docker and pull `mcr.microsoft.com/mssql/server:2022-latest` on first run.

## Layout

```
src/Inbox.Domain          entities, enums, Result/Error - no dependencies
src/Inbox.Application     contracts, validators, service interfaces - depends on Domain only
src/Inbox.Infrastructure  EF Core, Identity, auth and agent services, seeder
src/Inbox.Api             controllers, middleware, configuration, DI wiring
tests/                    Domain.Tests, Application.Tests, Infrastructure.Tests, IntegrationTests
tests/Fixtures            recorded WhatsApp webhook payloads, shared by both test projects
```

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| `Connection string 'Default' is not configured` | Not running as Development, or the settings file/secrets are missing. See [Running it](#running-it). |
| `Jwt:SigningKey must be at least 32 characters` | `Jwt:SigningKey` missing or too short. The app refuses to serve requests without it. |
| `/health` ok but `/health/ready` failing | The readiness check is the database check: the connection string points somewhere unreachable. |
| `401` with `auth.account_locked` | 5 wrong passwords in a row locks the account for 15 minutes. An admin can reset the password. |
| `404` on `/api/dev/simulate-inbound` | The dev endpoints are only mapped in Development; outside it the route does not exist. |
| `409` with `conversation.already_taken` | Another agent owns the conversation. Use `transfer` (owner or admin) instead of stealing it. |
| `422` with `conversation.window_closed` | The last inbound message is older than 24 hours. Send an approved template instead: `POST /api/conversations/{id}/template-messages`. The window follows the customer timestamp in the payload, so a webhook that arrives late cannot reopen it. |
| `403` on `/api/channels` | Agents cannot manage channels; only an admin can connect or disconnect one. |
| `401` on `/api/webhooks/whatsapp` | `X-Hub-Signature-256` is missing or does not match the raw body signed with the channel's app secret. A tunnel that rewrites the body would do the same thing. |
| `400` on `/api/webhooks/whatsapp` | The payload has no `metadata.phone_number_id`, so nothing identifies the sender. |
| WhatsApp deliveries arrive and are all dropped | The channel was disconnected, or its `externalAccountId` is not the phone number id from the Meta dashboard. Check the API log for the channel id. |
| A send is recorded as `Failed` with "GraphApiVersion is not configured" | `Meta:GraphApiVersion` is missing from configuration. |
| Hub connects then immediately disconnects, or 401 with `access_token` | The token must be passed as `?access_token=` on `/hubs/inbox`; a query token is only accepted on that route. |
| A thread event never arrives | Thread events only go to connections that called `OpenConversation` for that thread. Call it after the detail request succeeds, and again after switching threads. |