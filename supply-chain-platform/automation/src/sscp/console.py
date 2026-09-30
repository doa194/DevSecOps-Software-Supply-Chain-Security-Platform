"""Consistent, readable terminal output for long-running automation.

Every step prints what it is about to do and whether it worked, so a failed bootstrap
shows exactly where it stopped without reading tool output.
"""
from __future__ import annotations

import sys
import time
from contextlib import contextmanager
from typing import Iterator

_COLOURS = {"ok": "\033[32m", "warn": "\033[33m", "fail": "\033[31m", "info": "\033[36m", "dim": "\033[2m"}
_RESET = "\033[0m"
_use_colour = sys.stdout.isatty()


def _paint(kind: str, text: str) -> str:
    return f"{_COLOURS[kind]}{text}{_RESET}" if _use_colour else text


def heading(text: str) -> None:
    print(f"\n{_paint('info', '==')} {text}", flush=True)


def ok(text: str) -> None:
    print(f"  {_paint('ok', 'ok  ')} {text}", flush=True)


def warn(text: str) -> None:
    print(f"  {_paint('warn', 'warn')} {text}", flush=True)


def fail(text: str) -> None:
    print(f"  {_paint('fail', 'FAIL')} {text}", flush=True)


def info(text: str) -> None:
    print(f"  {_paint('dim', '..  ')} {text}", flush=True)


@contextmanager
def step(text: str) -> Iterator[None]:
    """Prints a step, times it and reports success or the failure reason."""
    started = time.monotonic()
    info(text)
    try:
        yield
    except Exception as error:  # re-raised after reporting
        fail(f"{text}: {error}")
        raise
    ok(f"{text} ({time.monotonic() - started:.1f}s)")
