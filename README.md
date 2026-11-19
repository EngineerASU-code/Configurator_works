# Configurator

Source code for the GASS Configurator desktop application (C# / .NET 8 / WPF).

The solution and project are in this repository root. Source, tests, runtime images, contributor instructions, and project documentation are tracked. Build outputs, IDE state, test artifacts, local reports, and credentials are excluded.

`MainWindow/`, `Modes/`, and `FirstScreen/` contain the WPF interface; `Core/` defines architecture and workspace contracts; `Application/`, `Services/`, and `Infrastructure/` implement workflows and storage. Shared UI resources live in `Themes/`, `Helpers/`, `ViewModels/`, and `Images/`.

See the [architecture map](docs/architecture.md) and [repository instructions](AGENTS.md). In Codex app, open the `configurator/` folder with `Configurator.slnx` to discover the [project skills](.agents/skills). Local Roadmaps and reports describe their recorded snapshots.

Use Windows, the .NET 10 SDK (for the `.slnx` solution), and the .NET 8 Windows Desktop Runtime. Restore from a fresh checkout:

```powershell
dotnet restore Configurator.slnx
```

Build from the repository root:

```powershell
dotnet build Configurator.csproj -c Release --no-restore
dotnet run --project Configurator.csproj
```

Run a group of tests or the full suite from the repository root:

```powershell
.\Run-Configurator-Tests.ps1 -Filter 'FullyQualifiedName~ProjectPackageAdversarialTests'
.\Run-Configurator-Tests.ps1
```

Both modes isolate and clean up test temporary files. The filtered run uses incremental restore and build checks and concise console output. The full run checks the SDK, restores the solution as needed, builds and tests in Release, and writes a TRX and transcript to `test-results/`.

To create a local runnable package:

```powershell
dotnet publish Configurator.csproj -c Release --no-restore -o artifacts/Configurator-release
```

Run `artifacts/Configurator-release/Configurator.exe` with its neighboring files. SQL workflows require a configured test database; routine checks must not write to a production database.
