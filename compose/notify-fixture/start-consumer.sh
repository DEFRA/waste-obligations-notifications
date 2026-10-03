#!/bin/sh
set -eu

# Only the isolated local fixture supplies these synthetic credentials.
NOTIFY__APIKEY=$(cat /fixtures/notify-api-key)
export NOTIFY__APIKEY
exec dotnet Consumer.dll
