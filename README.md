# .NET Web PoC (v5): named-arg fix for BuildImageFromDockerfileAsync

- Fixes the parameter order by using **named arguments** for the progress overload call.
- Keeps full try/catch and build-log capture; returns build diagnostics on failure, run output on success.
- Runner base: mcr.microsoft.com/dotnet/nightly/sdk:10.0-preview.

## Run
```bash
docker compose up --build
# Open http://localhost:8080
```
