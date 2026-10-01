#!/bin/sh
set -eu

# Only the isolated local fixture supplies these synthetic credentials.
Notify__ApiKey=$(cat /fixtures/notify-api-key)
export Notify__ApiKey
exec dotnet Consumer.dll
