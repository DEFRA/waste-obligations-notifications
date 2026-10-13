#!/usr/bin/env python3
"""Run a local command with a presence-aware Notify credential fallback."""
import os
import errno
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
        add_local_credential(environment, destination)
    # File content is data: no shell expansion, sourcing, evaluation or credential output.
    execute_command(sys.argv[2], sys.argv[2:], environment)


def execute_command(command, arguments, environment):
    if os.path.dirname(command):
        os.execve(command, arguments, environment)
        return
    first_error = None
    last_error = None
    for directory in os.get_exec_path(environment):
        executable = os.path.abspath(os.path.join(directory, command))
        try:
            os.execve(executable, arguments, environment)
        except OSError as error:
            last_error = error
            if error.errno not in (errno.ENOENT, errno.ENOTDIR) and first_error is None:
                first_error = error
    if first_error is not None:
        raise first_error
    if last_error is not None:
        raise last_error
    raise FileNotFoundError(command)


def add_local_credential(environment, destination):
    env_file = Path(__file__).resolve().parent.parent / ".env"
    if not env_file.is_file():
        return
    try:
        lines = env_file.read_text(encoding="utf-8-sig").splitlines()
    except (OSError, UnicodeError):
        raise SystemExit("Could not read the local Notify credential file.") from None
    for line in lines:
        name, separator, value = line.partition("=")
        if separator and name.strip() == "GovukNotify_ApiKey":
            environment[destination] = unquote(value.strip())


def unquote(value):
    if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
        return value[1:-1]
    return value


if __name__ == "__main__":
    main()
