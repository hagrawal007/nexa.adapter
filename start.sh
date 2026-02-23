#!/usr/bin/env sh
set -e

# Render default expected port is 10000 (configurable). [1](https://render.com/docs/web-services)
PORT="${PORT:-10000}"

# Bind to 0.0.0.0 so Render can route traffic to your service. [1](https://render.com/docs/web-services)
export ASPNETCORE_URLS="http://0.0.0.0:${PORT}"

# Optional: uncomment if you want explicit Production behavior
# export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"
# export DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Production}"

exec dotnet Nexa.Adapter.dll