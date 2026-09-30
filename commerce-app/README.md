# commerce-app

The secured workload of the Software Supply Chain Security Platform: a commerce and
order-management backend built as a modular monolith with four independently deployable
workers and a YARP gateway.

This repository is the **application repository**. It contains the source, tests,
Dockerfile and its validation workflow. It holds no CI secrets. The platform-owned
pipelines build one image per deployable registered for this application in the
platform's catalogue, from the Dockerfile target of the same name.

## Layout

| Path | Contents |
|---|---|
| `src/BuildingBlocks` | Shared kernel (domain primitives, contracts base types) and building blocks (outbox/inbox, signed messaging, security, telemetry) |
| `src/Modules/<Module>` | One project per business module plus its `Contracts` project |
| `src/Hosts` | `Commerce.Api` (the monolith host) and `Commerce.Gateway` (YARP) |
| `src/Workers` | Notification, audit, document and reporting workers |
| `tests` | Unit, architecture, integration (Testcontainers) and component tests |
| `config/publishers.yaml` | Which service may publish which integration events |

## Build and test

```bash
dotnet build Commerce.slnx -c Release
dotnet test --project tests/Commerce.UnitTests -c Release
dotnet test --project tests/Commerce.ArchitectureTests -c Release
dotnet test --project tests/Commerce.IntegrationTests -c Release   # needs Docker
dotnet test --project tests/Commerce.ComponentTests -c Release      # needs Docker
dotnet test --project tests/Commerce.Gateway.ComponentTests -c Release
docker build --target commerce-api -t commerce-api .
```

To run the workload on a workstation against the platform services, see the
workspace documentation: [workload architecture](../docs/workload-architecture.md).
