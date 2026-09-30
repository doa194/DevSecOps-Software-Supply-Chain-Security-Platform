# syntax=docker/dockerfile:1.27.0@sha256:bde3983e9c939224420ddaf6b784cc30e09b035a4dea01f581230c50809f372e
#
# Code-quality analysis image for .NET ("sonar-dotnet"), built by `sscp` and pushed to
# Harbor's platform-tools project. SonarQube analyses C# during compilation, so the image
# is the pinned .NET SDK plus a pinned Java runtime (the scanner engine runs on Java) and a
# pinned SonarScanner for .NET. The security zone runs it against a fetched commit.

ARG SDK_IMAGE
ARG JRE_IMAGE

FROM ${JRE_IMAGE} AS jre

FROM ${SDK_IMAGE}
ARG SCANNER_VERSION
COPY --from=jre /opt/java/openjdk /opt/java/openjdk
ENV JAVA_HOME=/opt/java/openjdk \
    PATH="/opt/java/openjdk/bin:/opt/sonar-scanner:${PATH}" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
RUN dotnet tool install dotnet-sonarscanner --version "${SCANNER_VERSION}" --tool-path /opt/sonar-scanner
