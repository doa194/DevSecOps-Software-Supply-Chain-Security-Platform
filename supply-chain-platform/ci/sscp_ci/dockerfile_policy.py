"""Base-image policy for application Dockerfiles, enforced by the build zone before building.

The build zone supplies the base images itself (digest-pinned copies from versions.yaml,
mirrored in Harbor) through two build arguments. A Dockerfile may therefore only start
stages from those arguments or from its own earlier stages. Anything else would let the
application repository choose what goes into a trusted image:

- `FROM some/image:tag` or `FROM ${OTHER_ARG}` (an unreviewed base image),
- `COPY --from=some/image` or `RUN --mount=...,from=some/image` (files from an image),
- `ADD https://...` (unpinned remote content).
"""
from __future__ import annotations

import re

ALLOWED_BASE_ARGUMENTS = ("SDK_IMAGE", "RUNTIME_IMAGE")
_ARGUMENT = re.compile(r"^\$\{?(?P<name>[A-Za-z_][A-Za-z0-9_]*)\}?$")


def _instructions(text: str) -> list[tuple[int, str]]:
    """Joins continuation lines and drops comments; returns (line number, instruction)."""
    result: list[tuple[int, str]] = []
    buffer, start = "", 0
    for number, raw in enumerate(text.splitlines(), start=1):
        line = raw.strip()
        if not buffer and (not line or line.startswith("#")):
            continue
        if not buffer:
            start = number
        if line.endswith("\\"):
            buffer += line[:-1] + " "
            continue
        result.append((start, (buffer + line).strip()))
        buffer = ""
    if buffer:
        result.append((start, buffer.strip()))
    return result


def violations(dockerfile: str) -> list[str]:
    stages: set[str] = set()
    found: list[str] = []
    for line, instruction in _instructions(dockerfile):
        keyword, _, rest = instruction.partition(" ")
        keyword = keyword.upper()
        tokens = [t for t in rest.split() if not t.startswith("--")]
        if keyword == "FROM" and tokens:
            image = tokens[0]
            argument = _ARGUMENT.match(image)
            allowed = (image.lower() in stages or image == "scratch"
                       or (argument is not None and argument.group("name") in ALLOWED_BASE_ARGUMENTS))
            if not allowed:
                found.append(f"line {line}: FROM {image} is not a platform base image; use ${{SDK_IMAGE}} or ${{RUNTIME_IMAGE}}")
            if len(tokens) >= 3 and tokens[1].upper() == "AS":
                stages.add(tokens[2].lower())
        elif keyword in ("COPY", "RUN"):
            for source in re.findall(r"(?:--from=|[,\s]from=)([^\s,]+)", " " + rest):
                if source.lower() not in stages and not source.isdigit():
                    found.append(f"line {line}: {keyword} reads from '{source}', which is not a stage of this Dockerfile")
        elif keyword == "ADD" and re.search(r"(?:^|\s)(https?|git)://|git@", rest):
            found.append(f"line {line}: ADD of remote content is not allowed; download in a build stage with a checksum instead")
    return found
