# ADR-0019: Email delivery with MailKit, Mailpit for local development

- **Status:** Accepted
- **Date:** 2026-11-14
- **Deciders:** @Ripan (M7 kickoff 2026-11-14; accepted with the instruction to implement M7, 2026-11-14)
- **Spec:** OBS-05 · ADR-0018 · OpenSpec change `add-operability-and-insight` (design.md decisions 5, 22)

## Context

OBS-05 notifications can be sent by email, so the orchestrator needs an SMTP client. .NET's
`System.Net.Mail.SmtpClient` still ships, but Microsoft's documentation marks it as not recommended for
new development: it lacks modern protocol support and is not being developed further. Developers and the
integration tests also need an SMTP server that accepts mail and lets a test read it back. The local stack
(`docker-compose.yml`) has Postgres and Keycloak only.

## Decision

- **MailKit** (pinned in `Directory.Packages.props`) sends email. It is the client that Microsoft's
  `SmtpClient` documentation points to. Settings come from `Baton:Notifications:Smtp`: host, port,
  security mode, user name, from address. The password comes from the environment, never from the
  repository.
- **Mailpit** (`axllent/mailpit`, pinned tag) is added to `docker-compose.yml` for local development (SMTP
  on 1025, web UI on 8025). It is also started by the integration tests through Testcontainers' generic
  container. The tests read delivered messages from its HTTP API. It is a development and test tool only.
  Deployments use the organization's mail relay.

## Consequences

**Positive**
- A maintained SMTP client with STARTTLS/SSL and authentication.
- Email delivery is tested end to end without an external service.

**Negative / risks**
- One more runtime dependency (MailKit and MimeKit). → it is widely used and maintained, and version bumps
  follow the usual one-bump-per-PR rule.
- One more container locally. → it is small and needs no configuration. Developers who never touch email
  can leave it stopped.

## Alternatives considered

| Option | Why not |
| --- | --- |
| `System.Net.Mail.SmtpClient` | Marked not recommended for new development by Microsoft |
| A provider HTTP API (SendGrid, SES, Graph) | Ties Baton to one vendor; most organizations already run an SMTP relay |
| Email only through a webhook to a mail service | Pushes the work to every deployment and contradicts OBS-05 "by email" |
| smtp4dev or MailHog for development | MailHog is unmaintained; Mailpit is its maintained successor with a simple HTTP API |

## References

- ADR-0018
- Microsoft docs, `System.Net.Mail.SmtpClient` remarks
