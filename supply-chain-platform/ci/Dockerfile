# syntax=docker/dockerfile:1.27.0@sha256:bde3983e9c939224420ddaf6b784cc30e09b035a4dea01f581230c50809f372e
#
# Job image of the security, build and trust zones ("ci-tools").
#
# Built by `sscp` and pushed to Harbor's platform-tools project; runners reference it by
# digest. It contains only pinned binaries copied out of pinned upstream images, the CI
# helper, the manifest of mirrored tool images and the platform's local root CA. Scanners
# are not in here: jobs run them as separate, digest-pinned containers.

ARG PYTHON_IMAGE
ARG DOCKER_CLI_IMAGE
ARG COSIGN_IMAGE
ARG CRANE_IMAGE

FROM ${DOCKER_CLI_IMAGE} AS docker
FROM ${COSIGN_IMAGE} AS cosign
FROM ${CRANE_IMAGE} AS crane

FROM ${PYTHON_IMAGE}
COPY --from=docker /usr/local/bin/docker /usr/local/bin/docker
# BuildKit client: builds honour `# syntax=` and the BUILDKIT_SYNTAX override.
COPY --from=docker /usr/local/libexec/docker/cli-plugins/docker-buildx /usr/local/libexec/docker/cli-plugins/docker-buildx
COPY --from=cosign /ko-app/cosign /usr/local/bin/cosign
COPY --from=crane /ko-app/crane /usr/local/bin/crane

# Trust the platform's local root CA for Gitea, Harbor, Vault, Keycloak and the Control Plane.
COPY ca.crt /usr/local/share/ca-certificates/sscp-root-ca.crt
RUN update-ca-certificates

COPY requirements.txt /opt/sscp-ci/requirements.txt
RUN pip install --no-cache-dir --require-hashes --only-binary=:all: -r /opt/sscp-ci/requirements.txt
COPY sscp_ci/ /opt/sscp-ci/sscp_ci/
COPY rules/ /opt/sscp-ci/rules/
COPY tool-images.json /opt/sscp-ci/sscp_ci/tool-images.json
RUN printf '#!/bin/sh\nexec python3 -m sscp_ci "$@"\n' > /usr/local/bin/sscp-ci && chmod 0755 /usr/local/bin/sscp-ci
ENV PYTHONPATH=/opt/sscp-ci \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1
