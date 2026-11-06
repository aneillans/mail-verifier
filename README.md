# mail-verifier
Mail Verification Toolkit

## Running with docker compose

Copy `.env.example` to `.env` and set `POSTGRES_PASSWORD` and `OIDC_CLIENT_SECRET`. Compose refuses to start until both are set.

## Verification settings (`Smtp` section)

| Setting | Default | Purpose |
| --- | --- | --- |
| `EhloHost` / `MailFromAddress` | `mailverifier.local` / `verify@<EhloHost>` | Identity used in the SMTP probe |
| `MaxParallelSessions` | 10 | SMTP sessions open at once |
| `MaxRecipientsPerSession` | 20 | `RCPT TO` probes sent over one connection to an MX host |
| `ConnectionLimits:<domain>` | Microsoft defaults | Max concurrent sessions for an MX host or recipient domain suffix (e.g. `Smtp__ConnectionLimits__google.com=3`) |
| `DefaultConnectionLimitPerMxHost` | 5 | Limit for MX hosts without a rule (0 = unlimited) |
| `MaxMxHostsToTry` | 3 | MX hosts tried, by preference, before a connection error is recorded |
| `CatchAllDetection` | true | Probe a random address; accepting domains are flagged "at risk (catch-all)" |
| `DnsCacheMinutes` | 30 | MX lookup cache lifetime |
| `CommandTimeoutMs` | 10000 | Timeout for connect and each SMTP command |

## Health checks

- `GET /healthz/live` – process is up.
- `GET /healthz` – database reachable and the verification queue is running.
- `dotnet MailVerifier.Web.dll --healthcheck` – probes `/healthz` from inside the container (used by the Dockerfile and compose `HEALTHCHECK`, since the chiseled image has no curl).

## Tests

```bash
dotnet test
```

The Postgres integration test starts a `postgres:18-alpine` container through Testcontainers, so it needs Docker or Podman. With rootless Podman, enable the socket and point Testcontainers at it:

```bash
systemctl --user enable --now podman.socket
export DOCKER_HOST=unix://$XDG_RUNTIME_DIR/podman/podman.sock
```
