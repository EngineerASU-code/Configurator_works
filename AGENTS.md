# GASS Configurator

## Stack and repository

Windows WPF, C# and .NET 8. Use the .NET 10 SDK for the SLNX solution and the .NET 8 Windows Desktop Runtime. The [README](README.md) contains build, run, and test commands. CI builds and tests Debug and Release on Windows. See the [architecture map](docs/architecture.md) for entry points.

`MainWindow/`, `FirstScreen/`, and `Modes/` contain screens. `Core/` contains domain rules and contracts. `Application/`, `Services/`, and `Infrastructure/` implement workflows and storage. Follow nearby C# and XAML conventions. Build diagnostics provide the existing static checks.

## Workspace and feature status

- Implemented: Online access to SQL Server, including legacy SQL projects. Use `IProjectWorkspace` for project data and `SqlProviderGuard` for SQL connection creation and opening.
- Implemented: Offline snapshots in local `.cfgproj` packages. Offline must never access SQL. Preserve integrity checks, reopen behavior, and recovery from interrupted writes.
- Implemented screens include SQL data operations, object architecture and project editing, including bulk object entry.
- Planned: Compare and selective synchronization, with cell-level differences and transfer of selected rows and configuration objects. [Synchronization contracts](Core/Synchronization/) cover plans, preview, confirmation, and stale-plan guards. Integrated Compare and apply are not established by those contracts.
- Deferred: the [FB editor entry](MainWindow/MainWindow.xaml) is a placeholder. Editing functional blocks is unavailable.

## Quality checks

Use [Run-Configurator-Tests.ps1](Run-Configurator-Tests.ps1) for filtered or full xUnit runs. Keep SQL credentials and local connection settings outside Git and use a dedicated SQL Server or LocalDB database for integration. Record automated tests, WPF acceptance, and SQL integration separately. Use [initial-setup](.agents/skills/initial-setup/SKILL.md) to establish the local quality checks and [task-review](.agents/skills/task-review/SKILL.md) for final GASS acceptance.

Historical Roadmaps and reports describe their recorded snapshot. Compare the Graphify commit with HEAD before using its relationships. Inspect changed and new files in the working tree.
