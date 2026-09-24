# Contributing to ForwardTrust

## Development

Install the .NET 8 SDK selected by `global.json`, then run commands from the repository root:

```powershell
dotnet restore KeelMatrix.ForwardTrust.sln
dotnet build KeelMatrix.ForwardTrust.sln -c Release --no-restore
dotnet test KeelMatrix.ForwardTrust.sln -c Release --no-build
dotnet format KeelMatrix.ForwardTrust.sln --verify-no-changes
```

The shipping API is in `src/KeelMatrix.ForwardTrust`. Integration and contract tests are in `tests/KeelMatrix.ForwardTrust.Tests`; the package consumer is under `smoke/ForwardTrust.Consumer`.

## Pull requests

Include focused tests, updated documentation for changed behavior, and package-consumer evidence when packaging changes. Keep the public API baseline intentional. Do not add production credentials, real customer data, or generated package artifacts.
