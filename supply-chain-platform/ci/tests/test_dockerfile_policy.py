"""The build zone must refuse any Dockerfile that chooses its own base images or pulls
content from outside the build context."""
from pathlib import Path

from sscp_ci import dockerfile_policy

COMMERCE_DOCKERFILE = Path(__file__).resolve().parents[3] / "commerce-app" / "Dockerfile"


def test_the_commerce_dockerfile_complies():
    assert dockerfile_policy.violations(COMMERCE_DOCKERFILE.read_text(encoding="utf-8")) == []


def test_stages_built_from_platform_arguments_and_earlier_stages_are_allowed():
    dockerfile = """
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk@sha256:abc
ARG RUNTIME_IMAGE
FROM ${SDK_IMAGE} AS build
RUN dotnet publish -o /app
FROM $RUNTIME_IMAGE AS runtime-base
FROM runtime-base AS api
COPY --from=build /app .
"""
    assert dockerfile_policy.violations(dockerfile) == []


def test_an_unreviewed_base_image_is_refused():
    found = dockerfile_policy.violations("FROM ubuntu:latest\nRUN echo hi\n")

    assert len(found) == 1 and "ubuntu:latest" in found[0]


def test_a_base_image_from_any_other_build_argument_is_refused():
    assert dockerfile_policy.violations("ARG BASE=alpine\nFROM ${BASE}\n")


def test_copying_or_mounting_files_from_an_external_image_is_refused():
    dockerfile = """FROM ${SDK_IMAGE} AS build
COPY --from=evil/tools:1 /bin/tool /usr/bin/tool
RUN --mount=type=bind,from=evil/other,source=/x,target=/y true
"""
    assert len(dockerfile_policy.violations(dockerfile)) == 2


def test_remote_add_is_refused_even_across_continuation_lines():
    dockerfile = "FROM ${RUNTIME_IMAGE}\nADD \\\n  https://example.test/payload.sh /payload.sh\n"

    assert dockerfile_policy.violations(dockerfile)
