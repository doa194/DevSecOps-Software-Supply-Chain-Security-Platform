"""Runs external tools (docker, kubectl, helm, git) and turns failures into clear errors."""
from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path
from typing import Mapping, Sequence

from sscp import paths


class CommandError(RuntimeError):
    def __init__(self, command: Sequence[str], returncode: int, output: str):
        self.command = list(command)
        self.returncode = returncode
        self.output = output
        tail = "\n".join(output.strip().splitlines()[-15:])
        super().__init__(f"`{' '.join(command[:4])}...` exited with {returncode}\n{tail}")


def which(tool: str) -> str | None:
    """Finds a tool, preferring the workspace's pinned copy in .tools/bin."""
    for candidate in (paths.TOOLS_BIN / f"{tool}.exe", paths.TOOLS_BIN / tool):
        if candidate.exists():
            return str(candidate)
    return shutil.which(tool)


def run(
    command: Sequence[str],
    *,
    check: bool = True,
    input_text: str | None = None,
    env: Mapping[str, str] | None = None,
    cwd: Path | None = None,
    timeout: float | None = None,
) -> subprocess.CompletedProcess[str]:
    """Runs a command and returns its output. Raises CommandError on failure when check=True."""
    resolved = list(command)
    located = which(resolved[0])
    if located:
        resolved[0] = located
    merged_env = {**os.environ, **(env or {})}
    result = subprocess.run(
        resolved,
        input=input_text,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=merged_env,
        cwd=cwd,
        timeout=timeout,
    )
    if check and result.returncode != 0:
        raise CommandError(command, result.returncode, result.stdout + result.stderr)
    return result


def output(command: Sequence[str], **kwargs) -> str:
    return run(command, **kwargs).stdout.strip()
