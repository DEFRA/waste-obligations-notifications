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


def notify_environment(root):
    environment = os.environ.copy()
    key = local_key(root)
    if key is not None:
        environment.setdefault("GOVUKNOTIFY_APIKEY", key)
        # .NET configuration names are case insensitive; Compose uses uppercase.
        if not any(name.casefold() == "notify__apikey" for name in environment):
            environment["Notify__ApiKey"] = key
    return environment


def main():
    # Select a fixed operation; never forward caller-supplied executable or arguments.
    if len(sys.argv) != 2 or sys.argv[1] not in ("run", "preview"):
        sys.exit("Usage: python3 scripts/with-notify-env.py {run|preview}")
    root = Path(__file__).resolve().parent.parent
    commands = {
        "run": ["dotnet", "run", "--project", "src/Consumer"],
        "preview": [
            "dotnet", "test", "--test-modules",
            "tests/Consumer.IntegrationTests/bin/Debug/net10.0/Consumer.IntegrationTests.dll",
            "--no-build", "--", "--filter-class", "*NotifySubmissionPreviewTests",
        ],
    }
    return subprocess.run(commands[sys.argv[1]], cwd=root, env=notify_environment(root), check=False).returncode


if __name__ == "__main__":
    sys.exit(main())
