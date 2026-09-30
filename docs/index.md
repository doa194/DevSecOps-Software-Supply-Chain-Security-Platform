# Documentation

This is the map of the platform's documentation. The [README](../README.md) gives the short
version: what the platform is, and how to start it. The documents below explain each part in
depth.

If you are new, read these three in order:

1. [architecture-overview.md](architecture-overview.md): the parts and how they fit together.
2. [end-to-end-flow.md](end-to-end-flow.md): one change followed from commit to running pod.
3. [setup-guide.md](setup-guide.md): run it yourself.

Unfamiliar terms are explained in the [glossary](glossary.md).

## Find the document for your question

| Question | Document |
|---|---|
| What are the parts of the platform, and how do they connect? | [architecture-overview.md](architecture-overview.md) |
| What happens, step by step, when a developer's change is merged and released? | [end-to-end-flow.md](end-to-end-flow.md) |
| How do I install, start, stop and remove it? | [setup-guide.md](setup-guide.md) |
| What does each `sscp` command and option do? | [cli-reference.md](cli-reference.md) |
| Where is a setting, a version pin or a generated file? | [configuration-reference.md](configuration-reference.md) |
| Where is the code for something, and how is the repository laid out? | [codebase-guide.md](codebase-guide.md) |
| What does a term mean? | [glossary.md](glossary.md) |

## Trust and separation

| Question | Document |
|---|---|
| Which parts trust which, and what stops a compromised part from spreading? | [trust-boundaries.md](trust-boundaries.md) |
| Who owns each repository, and who may change what? | [repository-boundaries.md](repository-boundaries.md) |
| Who is who, and what may each identity do? | [identity-and-authorization.md](identity-and-authorization.md) |
| Where do secrets live, and how do they reach the programs that use them? | [secret-management.md](secret-management.md) |
| What could an attacker try, and which control stops each attack? | [threat-model.md](threat-model.md) |

## From source to trusted artifact

| Question | Document |
|---|---|
| What do the pipelines do, and on which runner? | [ci-pipelines.md](ci-pipelines.md) |
| How is each scanner run, and how does its report become evidence? | [security-scanning.md](security-scanning.md) |
| How are the built images tested while they run? | [dynamic-security-testing.md](dynamic-security-testing.md) |
| What does the Security Control Plane do, and what is its API? | [security-control-plane.md](security-control-plane.md) |
| What must be true before an artifact is trusted? | [trust-policy.md](trust-policy.md) |
| How is a known risk accepted temporarily? | [risk-exceptions.md](risk-exceptions.md) |
| How are images stored, and who may push where? | [artifact-registry.md](artifact-registry.md) |
| How are releases signed, attested and promoted? | [release-signing.md](release-signing.md) |

## From trusted artifact to running workload

| Question | Document |
|---|---|
| How does a release reach the cluster, and what does admission control check? | [gitops-and-admission.md](gitops-and-admission.md) |
| How are workloads separated inside the cluster? | [kubernetes-security.md](kubernetes-security.md) |
| How can I see what the platform decided and what is happening at runtime? | [observability.md](observability.md) |

## The commerce workload

| Question | Document |
|---|---|
| What is the application being protected, and how is it built? | [workload-architecture.md](workload-architecture.md) |
| How do its parts share data and events safely? | [messaging-and-data.md](messaging-and-data.md) |

## Quality, operations and limits

| Question | Document |
|---|---|
| How is everything tested, and how do I run the tests? | [testing-strategy.md](testing-strategy.md) |
| What happens when a component fails, and how do I recover? | [failure-and-recovery.md](failure-and-recovery.md) |
| Something is broken. What do I do? | [troubleshooting.md](troubleshooting.md) |
| Why was it built this way, and what were the alternatives? | [design-decisions.md](design-decisions.md) |
| What is simplified for a workstation, and what would production need? | [production-considerations.md](production-considerations.md) |

## Reading paths

| If you are… | Read |
|---|---|
| **evaluating the project** | README → [architecture-overview](architecture-overview.md) → [end-to-end-flow](end-to-end-flow.md) → [threat-model](threat-model.md) → [design-decisions](design-decisions.md) |
| **running it for the first time** | [setup-guide](setup-guide.md) → [cli-reference](cli-reference.md) → [troubleshooting](troubleshooting.md) |
| **a security reviewer** | [trust-boundaries](trust-boundaries.md) → [threat-model](threat-model.md) → [trust-policy](trust-policy.md) → [release-signing](release-signing.md) → [gitops-and-admission](gitops-and-admission.md) → [production-considerations](production-considerations.md) |
| **changing the code** | [codebase-guide](codebase-guide.md) → [testing-strategy](testing-strategy.md) → [configuration-reference](configuration-reference.md) |
| **operating it** | [observability](observability.md) → [failure-and-recovery](failure-and-recovery.md) → [troubleshooting](troubleshooting.md) |
