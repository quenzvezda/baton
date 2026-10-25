# ADR-0014: People authenticate with OIDC bearer tokens

- **Status:** Accepted
- **Date:** 2026-10-25
- **Deciders:** @Ripan (owner review of M2, 2026-10-25)
- **Spec:** §3 Security ("OIDC login with Viewer / Operator / Admin roles") · NFR-Security · OpenSpec change `add-host-registry`

## Context

Every Control Plane endpoint must be protected, and three roles separate reading, operating and
administering. The organization's identity provider is not fixed yet, and the web console (M5) is not
the only client: scripts and CI call the API too.

## Decision

The Control Plane API is an **OAuth 2.0 resource server accepting JWT bearer tokens** from any OpenID
Connect provider, using `Microsoft.AspNetCore.Authentication.JwtBearer`.

- The provider is configured with the standard `Authentication:Schemes:Bearer` section (`Authority`,
  `ValidAudiences`), so no provider-specific code exists.
- `Baton:Auth:RoleClaimType` (default `roles`) names the claim carrying roles, and
  `Baton:Auth:Roles:Viewer|Operator|Admin` list the claim values that grant each role. Roles are
  hierarchical: Admin implies Operator implies Viewer.
- Every endpoint declares a policy (`Viewer`, `Operator` or `Admin`) or is explicitly anonymous
  (`/healthz`); a test fails when one does not. The fallback policy requires Viewer.
- The web console (M5) signs people in with the OIDC authorization-code flow and calls the API with
  their access token.
- Local development and tests sign tokens with a local key (`dotnet user-jwts`, test helpers).

## Consequences

**Positive**
- Works with Entra ID, Keycloak, Okta or any OIDC provider by configuration only.
- One authentication model for the console, scripts and CI.

**Negative / risks**
- A misconfigured role mapping locks everyone out or lets everyone in → mapping is explicit (no
  default claim values in production config) and covered by API tests.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Cookie sign-in on the API | The API also serves non-browser clients; cookies belong in the web console. |
| Provider SDK (e.g. Microsoft.Identity.Web) | Ties the platform to one provider. |
| API keys for people | No SSO, no central offboarding. |
