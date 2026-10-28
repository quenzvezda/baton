# ADR-0017: Keycloak as the local development identity provider

- **Status:** Accepted
- **Date:** 2026-10-28
- **Deciders:** @Ripan (M5 kickoff 2026-10-28)
- **Spec:** §3 Security · ADR-0014 · OpenSpec change `add-web-console-and-monitoring` (task 3.1)

## Context

The web console signs people in with the OIDC authorization-code flow (ADR-0012, ADR-0014). Until M5 the
API was exercised with tokens from `dotnet user-jwts` and test helpers, which cannot do an interactive
browser sign-in. Developers need a real OIDC provider locally, without depending on the organization's
production IdP (not fixed yet).

## Decision

`docker-compose.yml` runs **Keycloak** (dev mode, port 8080) next to Postgres, importing the realm in
`deploy/keycloak/baton-realm.json` on start:

- realm `baton`, public client `baton-web` (authorization code + PKCE, redirect `http://localhost:5300/*`)
  whose access tokens carry audience `baton-api` and a `roles` claim;
- realm roles `baton.viewer`, `baton.operator`, `baton.admin` and one user per role
  (`viewer` / `operator` / `admin`, password = user name, development only).

The API's and the console's `appsettings.Development.json` point at that realm. Production configures
its own IdP by the same settings; nothing in the code is Keycloak-specific.

## Consequences

**Positive**
- The real sign-in, token forwarding and role mapping run locally end to end.
- Same container usable by CI or demos.

**Negative / risks**
- One more local container (~500 MB image) → development only; never part of a deployment.
- Committed dev passwords → only valid against the local container, documented as such.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Dev-only fake sign-in minting local JWTs | Skips the authorization-code flow the console uses in production. |
| A hosted IdP tenant | Needs accounts and network access for every developer. |
| Duende IdentityServer / OpenIddict in-repo | More code to own; licensing (Duende). |
