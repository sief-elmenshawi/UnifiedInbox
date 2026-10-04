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
| `POST` | `/api/dev/simulate-inbound` | Development only, admin only. |

## Tests

```powershell
dotnet test UnifiedInbox.sln                                        # everything
dotnet test tests\Inbox.Domain.Tests                                # fast, no Docker
dotnet test UnifiedInbox.sln --filter "FullyQualifiedName~AuthFlowTests"
```

The integration tests run the real API against a throwaway SQL Server container (Testcontainers), so
they need Docker and pull `mcr.microsoft.com/mssql/server:2022-latest` on first run.

## Layout

```
src/Inbox.Domain          entities, enums, Result/Error - no dependencies
src/Inbox.Application     contracts, validators, service interfaces - depends on Domain only
src/Inbox.Infrastructure  EF Core, Identity, auth and agent services, seeder
src/Inbox.Api             controllers, middleware, configuration, DI wiring
tests/                    Domain.Tests, Application.Tests, IntegrationTests (WebApplicationFactory)
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
| `422` with `conversation.window_closed` | The last inbound message is older than 24 hours; templates are a later phase. |