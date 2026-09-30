# Glossary

Plain-language explanations of the terms used throughout this documentation. Terms are
grouped by topic; within a group they are in alphabetical order.

## Software supply chain

| Term | Meaning |
|---|---|
| **Artifact** | Something a build produces and the platform tracks. Here: one container image per deployable, identified by its digest. |
| **Attestation** | A signed statement *about* an artifact, for example "this image was built from commit X" (provenance) or "this image passed the trust policy" (trust decision). It is attached to the image and names the image's digest as its subject. |
| **Candidate** | An image that has been built and pushed but not yet trusted. Candidates live in the Harbor project `commerce-candidates`. |
| **Deployable** | One independently deployable service of the application, for example `commerce-api` or `audit-worker`. The platform's catalogue (`policy/applications.yaml`) lists them; each becomes one image. |
| **Digest** | The SHA-256 hash of an image manifest, written `sha256:…`. It identifies an image's exact content: if one byte changes, the digest changes. Tags such as `v1.0.0` can be moved to another image; a digest cannot. Every decision in this platform is made about a digest. |
| **Evidence** | A raw scanner or test report submitted to the Security Control Plane, together with what it describes (commit, image digest) and whether the tool completed. |
| **Promotion** | Copying an approved image, unchanged and by digest, from the candidate project to the trusted project (`commerce-trusted`). |
| **Provenance** | A record of how an artifact was built: source repository, commit, builder, pipeline run and times. Here it uses the SLSA v1 provenance format. |
| **Referrer (OCI)** | An artifact in a registry that points at another artifact's digest. Signatures and attestations are stored as referrers of the image they describe, so they travel with it and can be listed through the registry's referrers API. |
| **Release** | A protected Git tag `v<major>.<minor>.<patch>` on the application repository. It asks the platform to sign, promote and deploy the images the main pipeline built for that commit. |
| **SBOM** | Software Bill of Materials: the list of components (packages, libraries, operating-system packages) inside an image. Here: CycloneDX JSON produced by Syft. |
| **Signature** | A cryptographic proof, made with the release key, that the platform approved an image digest. Kubernetes checks it before running the image. |
| **SLSA** | *Supply-chain Levels for Software Artifacts*, a framework for build integrity. This platform uses SLSA's provenance *format* but does not claim a SLSA *level* (see [release-signing.md](release-signing.md#why-slsa-style)). |
| **Trust decision** | The Security Control Plane's verdict for one artifact under the trust policy: `PASS`, `PASS_WITH_EXCEPTION` or `FAIL`. |
| **Trusted image** | An image in `commerce-trusted`: promoted after a positive decision, signed and attested. Only these can run in the `commerce` namespace. |

## Platform components

| Term | Meaning |
|---|---|
| **CI helper (`sscp-ci`)** | The Python command that platform pipelines call to fetch source, run scanners, build images, talk to the Control Plane and sign releases. Lives in `supply-chain-platform/ci/`. |
| **Control Plane** | Short for *Security Control Plane*: the .NET service that stores evidence, applies the trust policy, issues signing grants and keeps the audit log. See [security-control-plane.md](security-control-plane.md). |
| **`sscp` automation** | The Python command-line tool that creates, configures, verifies and removes the whole platform on a workstation. See [cli-reference.md](cli-reference.md). |
| **Software factory** | Everything that turns source code into trusted artifacts: Gitea, the CI runners, Harbor, Vault, Keycloak, SonarQube and the Control Plane. It runs on Docker Compose. |
| **Deployment target** | The kind Kubernetes cluster where trusted releases run, together with Argo CD, Kyverno, External Secrets and observability. |
| **Trust policy** | The rules the Control Plane applies (`policy/trust-policy.yaml`): which evidence is mandatory, which findings block, remediation windows, exception rules. See [trust-policy.md](trust-policy.md). |

## CI and trust zones

| Term | Meaning |
|---|---|
| **Runner** | A Gitea Actions agent that executes pipeline jobs. The platform has four, one per trust zone. |
| **Run binding** | The Control Plane accepts pipeline calls only from the exact workflow run it started for that build or release. Evidence from any other run is refused. |
| **Signing grant** | A one-time, few-minutes credential the Control Plane issues to the trust zone for one approved release. It is the only way to obtain signing rights. See [release-signing.md](release-signing.md#who-can-sign). |
| **Trust zone (CI)** | A separately isolated CI environment with its own runner, Docker daemon, network address and credentials: *validation*, *security*, *build* and *trust*. Also, specifically, the zone that signs and promotes releases. See [trust-boundaries.md](trust-boundaries.md). |
| **Fail closed** | When a required control is missing or broken, the result is "not trusted" rather than "trusted by default". |

## Security terms

| Term | Meaning |
|---|---|
| **AppRole** | A Vault login method for machines: a `role_id` plus a `secret_id`. Here each CI zone has one, and Vault accepts it only from that zone's runner address. |
| **DAST** | Dynamic Application Security Testing: attacking a running application from outside, here with OWASP ZAP. |
| **Finding** | One issue reported by a scanner. The Control Plane gives each a stable **fingerprint** (for example `CVE-2024-1234|pkg:nuget/Example@1.0.0`) so it can be tracked across builds and matched by exceptions. |
| **Response wrapping** | A Vault feature that hands out a secret inside a single-use envelope token; the secret can be unwrapped exactly once. |
| **Risk exception** | A time-limited, separately approved permission for one known finding to pass its gate. See [risk-exceptions.md](risk-exceptions.md). |
| **SAST** | Static Application Security Testing: analysing source code without running it, here with Semgrep. |
| **STRIDE** | A threat-modelling checklist: Spoofing, Tampering, Repudiation, Information disclosure, Denial of service, Elevation of privilege. |
| **Transit engine (Vault)** | Vault's "encryption/signing as a service": the key never leaves Vault; callers send data and receive a signature. |

## Kubernetes terms

| Term | Meaning |
|---|---|
| **Admission control** | Checks the Kubernetes API server runs before it stores an object (for example a Pod). A refused object is never created. Here: Kyverno policies, one native `ValidatingAdmissionPolicy` and Pod Security Standards. |
| **Argo CD** | The GitOps controller: it watches Git repositories and makes the cluster match them. |
| **ExternalSecret** | An External Secrets Operator resource that names a Vault path; the operator reads it and creates the Kubernetes Secret. |
| **GitOps** | Deploying by committing desired state to Git and letting a controller apply it, instead of pushing changes into the cluster from CI. |
| **kind** | "Kubernetes in Docker": a Kubernetes cluster whose nodes are Docker containers. |
| **Kyverno** | A Kubernetes policy engine; here it verifies image signatures and attestations and enforces workload rules. |
| **NetworkPolicy** | A Kubernetes rule that allows or blocks network traffic between pods. The application namespaces deny everything by default. |
| **Pod Security Standard `restricted`** | Kubernetes' strictest built-in pod profile: no root, no privilege escalation, no host access, dropped capabilities. |

## Workload terms

| Term | Meaning |
|---|---|
| **Inbox** | A table in which a consumer records the messages it has processed, so a repeated delivery has no second effect. |
| **Modular monolith** | One deployable application internally split into modules with enforced boundaries. |
| **Outbox** | A table in which a service writes the events it will publish, in the same database transaction as the business change, so a change and its event can never disagree. |
| **Persona** | A predefined test user with fixed roles (for example `carol`, a customer, or `rita`, a risk owner). |
