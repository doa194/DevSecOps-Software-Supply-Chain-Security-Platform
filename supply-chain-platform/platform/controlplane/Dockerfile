# syntax=docker/dockerfile:1.27.0@sha256:bde3983e9c939224420ddaf6b784cc30e09b035a4dea01f581230c50809f372e
#
# Image of the Security Control Plane, built locally by `sscp up` (build context: the
# supply-chain-platform repository root).
#
# - Base images are pinned by digest; `sscp` passes the values from versions.yaml.
# - Restore runs in locked mode against signed nuget.org packages only (nuget.config).
# - The trust policy and application catalogue are copied in, so the policy a running
#   Control Plane applies is exactly the reviewed content of policy/ at build time.
# - The runtime image is chiseled: no shell, no package manager, non-root user.

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled@sha256:9651fa59abcdf177c30392cb44a820605ca5d618429ab37acbf6e7c644510b02

FROM ${SDK_IMAGE} AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    CI=true
WORKDIR /source

# Project files and lock files first, so the restore layer is reused while only code changes.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Sscp.ControlPlane.Domain/*.csproj src/Sscp.ControlPlane.Domain/packages.lock.json src/Sscp.ControlPlane.Domain/
COPY src/Sscp.ControlPlane.Application/*.csproj src/Sscp.ControlPlane.Application/packages.lock.json src/Sscp.ControlPlane.Application/
COPY src/Sscp.ControlPlane.Infrastructure/*.csproj src/Sscp.ControlPlane.Infrastructure/packages.lock.json src/Sscp.ControlPlane.Infrastructure/
COPY src/Sscp.ControlPlane.Api/*.csproj src/Sscp.ControlPlane.Api/packages.lock.json src/Sscp.ControlPlane.Api/
RUN dotnet restore src/Sscp.ControlPlane.Api/Sscp.ControlPlane.Api.csproj --locked-mode

COPY src/ src/
RUN dotnet publish src/Sscp.ControlPlane.Api/Sscp.ControlPlane.Api.csproj -c Release --no-restore -o /app

FROM ${RUNTIME_IMAGE}
WORKDIR /app
COPY --from=build /app .
COPY policy/ /policy/
USER $APP_UID
EXPOSE 8443 9464
ENTRYPOINT ["dotnet", "Sscp.ControlPlane.Api.dll"]
