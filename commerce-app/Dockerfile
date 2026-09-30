# syntax=docker/dockerfile:1.27.0@sha256:bde3983e9c939224420ddaf6b784cc30e09b035a4dea01f581230c50809f372e
#
# Builds all six deployables of the commerce workload from one locked restore.
#
# Supply-chain rules this file follows:
# - Base images are pinned by digest. The platform build passes the same values from its
#   versions.yaml as build arguments, and rejects a Dockerfile whose FROM lines bypass them.
# - `dotnet restore --locked-mode` fails if any package differs from packages.lock.json
#   (version or content hash), and nuget.config only allows signed nuget.org packages.
# - Runtime images are Ubuntu "chiseled": no shell, no package manager, non-root user.
#
# Build one deployable:  docker build --target commerce-api -t commerce-api .

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled@sha256:9651fa59abcdf177c30392cb44a820605ca5d618429ab37acbf6e7c644510b02

FROM ${SDK_IMAGE} AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    CI=true
WORKDIR /src
COPY . .
RUN dotnet restore src/Hosts/Commerce.Api/Commerce.Api.csproj --locked-mode \
 && dotnet restore src/Hosts/Commerce.Gateway/Commerce.Gateway.csproj --locked-mode \
 && dotnet restore src/Workers/Commerce.Workers.Notification/Commerce.Workers.Notification.csproj --locked-mode \
 && dotnet restore src/Workers/Commerce.Workers.Audit/Commerce.Workers.Audit.csproj --locked-mode \
 && dotnet restore src/Workers/Commerce.Workers.Documents/Commerce.Workers.Documents.csproj --locked-mode \
 && dotnet restore src/Workers/Commerce.Workers.Reporting/Commerce.Workers.Reporting.csproj --locked-mode
RUN dotnet publish src/Hosts/Commerce.Api -c Release --no-restore -p:UseAppHost=false -o /out/commerce-api \
 && dotnet publish src/Hosts/Commerce.Gateway -c Release --no-restore -p:UseAppHost=false -o /out/commerce-gateway \
 && dotnet publish src/Workers/Commerce.Workers.Notification -c Release --no-restore -p:UseAppHost=false -o /out/notification-worker \
 && dotnet publish src/Workers/Commerce.Workers.Audit -c Release --no-restore -p:UseAppHost=false -o /out/audit-worker \
 && dotnet publish src/Workers/Commerce.Workers.Documents -c Release --no-restore -p:UseAppHost=false -o /out/document-worker \
 && dotnet publish src/Workers/Commerce.Workers.Reporting -c Release --no-restore -p:UseAppHost=false -o /out/reporting-worker

# Shared runtime settings: listen on 8080 as the non-root "app" user.
FROM ${RUNTIME_IMAGE} AS runtime
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /app
EXPOSE 8080
USER $APP_UID

FROM runtime AS commerce-api
COPY --from=build --chown=root:root /out/commerce-api .
ENTRYPOINT ["dotnet", "Commerce.Api.dll"]

FROM runtime AS commerce-gateway
COPY --from=build --chown=root:root /out/commerce-gateway .
ENTRYPOINT ["dotnet", "Commerce.Gateway.dll"]

FROM runtime AS notification-worker
COPY --from=build --chown=root:root /out/notification-worker .
ENTRYPOINT ["dotnet", "Commerce.Workers.Notification.dll"]

FROM runtime AS audit-worker
COPY --from=build --chown=root:root /out/audit-worker .
ENTRYPOINT ["dotnet", "Commerce.Workers.Audit.dll"]

FROM runtime AS document-worker
COPY --from=build --chown=root:root /out/document-worker .
ENTRYPOINT ["dotnet", "Commerce.Workers.Documents.dll"]

FROM runtime AS reporting-worker
COPY --from=build --chown=root:root /out/reporting-worker .
ENTRYPOINT ["dotnet", "Commerce.Workers.Reporting.dll"]
