# Identity and authorization

This document answers two questions for every part of the system:

- **Who is it?** This is *identity* (authentication): how a person or a program proves who
  it is.
- **What may it do?** This is *authorization*: which actions that identity is allowed to
  take.

The platform has four identity systems. Each one protects one kind of resource:

| Identity system | Protects | Identities |
|---|---|---|
| **Gitea** accounts and teams | source code, branches, tags, pull requests, workflow runs | people (developers, maintainers, release manager, platform engineers) and four machine accounts |
| **Keycloak `platform` realm** | the Security Control Plane API | people (risk owners, security approvers, viewers, the platform administrator) and three CI zone clients |
| **Vault** auth methods | secrets, the signing key | CI zones, the Control Plane, the signing identity, cluster namespaces |
| **Keycloak `commerce` realm** | the commerce application's API | shop personas (customers, staff, administrator) |

Kubernetes service accounts are a fifth, internal kind of identity. They are covered in
[kubernetes-security.md](kubernetes-security.md#workload-identities).

## People

All users are test personas created by `sscp up`. Their passwords are generated randomly
and kept in `.local/secrets/bootstrap.json`, which is never committed:

- Gitea users: `gitea.user.<name>`;
- Keycloak users: `keycloak.<realm>.user.<name>`.

### Source control (Gitea)

Defined in `supply-chain-platform/platform/gitea/` and applied by `sscp up`:

| User | Team | May |
|---|---|---|
| `alice` | `commerce/developers` | push branches of `commerce-app` and open pull requests |
| `max` | `commerce/maintainers` | approve pull requests on `commerce-app` |
| `rhea` | `commerce/release-managers` | create protected `v*` release tags on `commerce-app` |
| `pat` | `platform/platform-engineers` | change the platform and GitOps repositories |
| `omar` | `platform/gitops-reviewers` | approve pull requests on `commerce-gitops` |

Branch protection and review rules are in
[repository-boundaries.md](repository-boundaries.md).

### Control Plane (Keycloak `platform` realm)

Defined in `supply-chain-platform/platform/keycloak/realms/platform.yaml`:

| User | Roles | May |
|---|---|---|
| `victor` | `platform-viewer` | read builds, evidence, decisions, releases, traces and the audit log |
| `rita` | `risk-owner`, `platform-viewer` | also request risk exceptions |
| `sean` | `security-approver`, `risk-owner`, `platform-viewer` | also approve, reject and revoke exceptions requested **by someone else** |
| `paula` | `platform-admin`, `platform-viewer` | also retry failed builds |

People get tokens through the public `sscp-cli` client with the password grant. Tokens live
5 minutes. The realm locks an account after five failed logins (brute-force protection) and
requires HTTPS for every request.

The exact rights of each role on each API route are listed in
[security-control-plane.md](security-control-plane.md#api). To get a token for one of these
users from the workspace:

```bash
cd supply-chain-platform
uv run python -c "from sscp.services import controlplane; print(controlplane.user_token('victor'))"
```

## Machine identities

Programs never use a person's account. Each has its own identity, with only the rights its
job needs:

| Identity | Kind | May | Cannot |
|---|---|---|---|
| `ci-security-zone`, `ci-build-zone`, `ci-trust-zone` | Keycloak confidential clients (client-credentials grant) | call the Control Plane routes of their own zone ([trust-boundaries.md](trust-boundaries.md)) | act as a person: approve exceptions, read traces |
| `ci-security`, `ci-build`, `ci-trust` | Vault AppRoles, accepted only from the zone runner's fixed address (172.30.0.22, .23, .24) | read `kv/ci/<zone>/*` | read another zone's secrets, sign |
| `controlplane` | Vault AppRole, accepted only from 172.30.0.14 | create single-use `secret_id`s for `trust-signer` | sign, read secrets |
| `trust-signer` | Vault AppRole, accepted only from the trust runner (172.30.0.24) | sign with the release key; read the promoter robot and GitOps bot token | exist outside a signing grant ([release-signing.md](release-signing.md#who-can-sign)) |
| `eso-<namespace>` | Vault JWT role for one namespace's `vault-secrets` service account | read that namespace's paths | read another namespace's paths ([secret-management.md](secret-management.md)) |
| `sscp-controlplane` | Gitea machine account | start platform workflow runs; set commit statuses | change code |
| `sscp-source-reader` | Gitea machine account | read `commerce-app` and `commerce-gitops` | write anything |
| `sscp-gitops-bot` | Gitea machine account | push to `commerce-gitops` `main` | be used without a signing grant (its token lives in `kv/ci/trust-signer/*`) |
| `sscp-argocd` | Gitea machine account | read the platform and GitOps repositories | write anything |
| `candidate-pusher`, `candidate-reader`, `promoter`, `cluster-puller` | Harbor robot accounts | one action on one project each ([artifact-registry.md](artifact-registry.md#robot-accounts)) | anything else |

The validation zone has **no** Vault identity, no Keycloak client and no Harbor robot. It
runs the application team's own workflow and must therefore hold no platform credentials.

**Why IP binding?** An AppRole's `secret_id` is a password. Binding it and its tokens to the
runner's fixed address (`secret_id_bound_cidrs`, `token_bound_cidrs`) means a leaked copy is
useless anywhere else. `sscp verify ci-isolation` and `sscp verify signing` test this from
other containers.

## The commerce application

The rest of this document covers how the commerce services authenticate and authorize their
users. The rules are implemented in the application repository, mainly in
`commerce-app/src/BuildingBlocks/Commerce.BuildingBlocks/Security/`.

### Personas

The `commerce` realm (`supply-chain-platform/platform/keycloak/realms/commerce.yaml`) has one
persona per role:

| User | Role | Typical actions |
|---|---|---|
| `carol`, `dave` | `customer` | browse, order, pay, download their own invoices |
| `sam` | `support-agent` | look up customers (personal data masked), read orders and documents |
| `cathy` | `catalog-manager` | create products and change prices |
| `ivan` | `inventory-clerk` | receive and adjust stock |
| `olga` | `order-manager` | read and manage any order, read reports |
| `fiona` | `finance` | read payments, issue refunds, read financial data |
| `aldo` | `auditor` | read and verify the audit trail |
| `ada` | `admin` | administer products, features and roles; no refunds |

### Tokens

Every service validates JWT access tokens itself: the gateway **and** each backend. A
backend therefore stays protected even if a request bypasses the gateway. The rules are
in
[`AuthenticationSetup.cs`](../commerce-app/src/BuildingBlocks/Commerce.BuildingBlocks/Security/AuthenticationSetup.cs):

| Check | Rule |
|---|---|
| Issuer | exactly `https://keycloak.sscp.test:9443/realms/commerce` |
| Audience | `commerce-api`; tokens issued for other clients are refused |
| Signature | RS256 only, with a key from the realm's published key set (JWKS). Unsigned (`alg: none`) and forged tokens are refused |
| Lifetime | the token must carry an expiry (`exp`); 30 seconds of clock difference is tolerated; access tokens live 5 minutes |
| Transport | signing keys are fetched over HTTPS, verified against the local CA |

Keycloak's hostname is fixed, so the issuer is the same whether the token was requested from
the host, a CI job or a pod. Keycloak puts roles in a nested `realm_access.roles` claim; the
services flatten it into role claims and ignore roles that Keycloak adds for its own use.

### Deny by default

The authorization fallback policy requires an authenticated user. An endpoint is public
only if it explicitly says `AllowAnonymous()`. The whole public surface is:

| Endpoint | Why it is public |
|---|---|
| `GET /api/catalog/products`, `GET /api/catalog/products/{id}` | storefront browsing: active products and public fields only |
| `/health/live`, `/health/ready` | Kubernetes probes; the gateway does not route them |

The gateway enforces the same allow-list independently. Any other route without a valid
token is answered with `401` before a backend is called.

### Roles and permissions

Keycloak assigns **roles** to users. Endpoints require **permissions**. One table maps
permissions to roles
([`Permissions.cs`](../commerce-app/src/BuildingBlocks/Commerce.BuildingBlocks/Security/Permissions.cs)),
so the whole access model can be reviewed in one place:

| Permission | Roles |
|---|---|
| `catalog:write` | catalog-manager, admin |
| `inventory:read` | inventory-clerk, order-manager, admin |
| `inventory:write` | inventory-clerk, admin |
| `orders:place` | customer |
| `orders:read-any` | order-manager, support-agent, admin |
| `orders:manage` | order-manager, admin |
| `customers:read-any` | support-agent, order-manager, admin |
| `payments:read-any` | finance, admin |
| `payments:refund` | finance only, deliberately not admin (separation of duties) |
| `documents:read-any` | support-agent, admin |
| `reports:read` | finance, order-manager, admin |
| `audit:read` | auditor, admin |
| `features:manage`, `roles:manage` | admin |
| `data:personal:read` | admin (clearance to see personal data unmasked) |
| `data:financial:read` | finance, admin |

**Separation of duties** means no single role can both control the system and move money.
An administrator can grant roles but cannot issue a refund, and cannot change their own
roles.

### Rules about specific records

Role checks are not enough when a customer asks for one specific record:

| Resource | Rule | On denial |
|---|---|---|
| Order | the owner, or `orders:read-any` | `404` and an `authorization.denied` security event |
| Order cancellation | the owner before payment (after payment only if the `Orders.CancelAfterPayment` feature flag is on); order managers until fulfilment | `403` or `404` |
| Document | the owning customer, or `documents:read-any`; staff downloads are audited | `404` |
| Customer profile | `/me` endpoints use the token's subject; staff lookups need `customers:read-any` and are audited | `403` |
| Role change | nobody may change their own roles | `403` and a `policy.violation` event; Keycloak is not called |

Records that exist but belong to someone else answer `404`, not `403`. A customer therefore
cannot find out which order or document ids exist by probing.

### Data classification and masking

Fields are classified where they are defined, with `[PersonalData]`, `[FinancialData]` or
`[SecretData]`. The classification drives two mechanisms:

- **Logs.** Classified values are replaced before a log line leaves the process. Personal
  data becomes an HMAC token, a keyed hash that still lets the same person's entries be
  correlated without revealing the value. Financial and secret data are erased.
- **Responses.** A JSON serializer rule masks classified fields (for example `c***`) unless
  the caller owns the record or holds the matching clearance permission. Secret fields are
  never serialised at all.

Example: a support agent looking up a customer sees `"email": "c***"`. The customer and an
administrator see the full address.

### Security events

Security-relevant situations are logged with fixed event names and counted in the
`commerce_security_events_total` metric, which dashboards and alerts use
([observability.md](observability.md)):

| Event | Emitted when |
|---|---|
| `authentication.failed` | a token is missing, expired, forged or for the wrong audience |
| `authorization.denied` | a role rule or a record rule refuses a request |
| `identity.role_changed` | an administrator changes someone's roles |
| `privileged.operation` | refunds, unmasked personal-data reads, staff document downloads, feature toggles |
| `policy.violation` | a guarded rule is attempted, such as changing one's own roles |
| `integration.message_rejected` | an event message fails its signature or publisher check ([messaging-and-data.md](messaging-and-data.md)) |
| `ratelimit.rejected` | the gateway rejects a caller for exceeding a limit |

### Gateway limits

| Limit | Value |
|---|---|
| Anonymous requests | 60 per minute per client address |
| Authenticated requests | 300 per minute per user |
| Order placement | 10 per minute per user |
| Request body | 1 MiB; 6 MiB for document uploads |

Client-supplied `X-Forwarded-*` headers are replaced, never appended to. A client therefore
cannot fake its address to escape the anonymous rate limit.

## How this is tested

| Behaviour | Test |
|---|---|
| Permission table, masking decisions, self role change | `commerce-app/tests/Commerce.UnitTests/Security/SecurityPolicyTests.cs` |
| Real Keycloak tokens: audience, tampering, missing token | `commerce-app/tests/Commerce.IntegrationTests/Keycloak/KeycloakTokenValidationTests.cs` |
| Record rules, masking, separation of duties, security events, over the real HTTP pipeline | `commerce-app/tests/Commerce.ComponentTests/ApiSecurityTests.cs` |
| Gateway allow-list, rate limits, forwarded headers | `commerce-app/tests/Commerce.Gateway.ComponentTests/GatewayBoundaryTests.cs` |
| The whole model against the built images, with real Keycloak tokens | the authorization suite in every main build ([dynamic-security-testing.md](dynamic-security-testing.md)) |
| Control Plane caller rules (zones, people, run binding) | `supply-chain-platform/tests/Sscp.ControlPlane.ComponentTests` |
| AppRole address binding, single-use signing grants | `sscp verify ci-isolation`, `sscp verify signing` |

## Limitations

- **The password grant is used for people.** The `sscp-cli` and `commerce-cli` clients let a
  script exchange a username and password for a token. This keeps local testing simple. A
  browser application would use the authorization-code flow with PKCE, and production would
  add multi-factor authentication.
- **No single sign-on between Gitea and Keycloak.** Gitea keeps its own accounts, so a
  person's Gitea and Control Plane identities are linked only by name.
- **Test personas share one workstation.** Separation of duties between `rita` and `sean`,
  or `alice` and `max`, is enforced by the systems. On this workstation, however, one
  operator can read every generated password in `.local/secrets`.
