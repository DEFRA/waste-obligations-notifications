#!/usr/bin/env python3
"""Run a local command with a presence-aware Notify credential fallback."""
import os
from pathlib import Path
import sys


def main():
    if len(sys.argv) < 3 or sys.argv[1] not in ("preview", "consumer"):
        raise SystemExit("Usage: python3 scripts/with-notify-key.py preview|consumer COMMAND [ARG ...]")
    destination = "GOVUKNOTIFY_APIKEY" if sys.argv[1] == "preview" else "Notify__ApiKey"
    environment = os.environ.copy()
    configured = (
        any(name.casefold() == destination.casefold() for name in environment)
        if destination == "Notify__ApiKey"
        else destination in environment
    )
    if not configured:
        env_file = Path(__file__).resolve().parent.parent / ".env"
        if env_file.is_file():
            try:
                lines = env_file.read_text(encoding="utf-8-sig").splitlines()
            except (OSError, UnicodeError):
                raise SystemExit("Could not read the local Notify credential file.") from None
            for line in lines:
                name, separator, value = line.partition("=")
                if separator and name.strip() == "GovukNotify_ApiKey":
                    value = value.strip()
                    if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
                        value = value[1:-1]
                    environment[destination] = value
    # File content is data: no shell expansion, sourcing, evaluation or credential output.
    os.execvpe(sys.argv[2], sys.argv[2:], environment)


if __name__ == "__main__":
    main()
