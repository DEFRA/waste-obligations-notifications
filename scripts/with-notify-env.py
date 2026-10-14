#!/usr/bin/env python3
"""Apply the ignored root .env Notify fallback without evaluating shell syntax."""
import os
from pathlib import Path
import subprocess
import sys


def local_key(root):
    path = root / ".env"
    if not path.is_file():
        return None
    for line in path.read_text(encoding="utf-8").splitlines():
        name, separator, value = line.strip().partition("=")
        if separator and name.strip() == "GovukNotify_ApiKey":
            value = value.strip()
            if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
                value = value[1:-1]
            return value
    return None


def main():
    if len(sys.argv) < 2:
        sys.exit("Usage: python3 scripts/with-notify-env.py COMMAND [ARG ...]")
    environment = os.environ.copy()
    key = local_key(Path(__file__).resolve().parent.parent)
    if key is not None:
        environment.setdefault("GOVUKNOTIFY_APIKEY", key)
        # .NET configuration names are case insensitive; Compose uses uppercase.
        if not any(name.casefold() == "notify__apikey" for name in environment):
            environment["Notify__ApiKey"] = key
    return subprocess.call(sys.argv[1:], env=environment)


if __name__ == "__main__":
    sys.exit(main())
