# Configurator architecture map

| Area | Start here |
| --- | --- |
| Startup and screens | [App.xaml.cs](../App.xaml.cs), [MainWindow](../MainWindow/), [Modes](../Modes/) |
| Domain rules and use cases | [Core/Architecture](../Core/Architecture/), [Application](../Application/), [Services](../Services/) |
| Project workspace and source selection | [IProjectWorkspace](../Core/Projects/IProjectWorkspace.cs), [ProjectDataSourceContext](../Core/Projects/ProjectDataSourceContext.cs) |
| Online SQL boundary | [SqlProviderGuard](../Core/Projects/SqlProviderGuard.cs), [Infrastructure](../Infrastructure/) |
| Offline packages and recovery | [ProjectPackageService](../Infrastructure/Projects/ProjectPackageService.cs), [MainWindow.LocalProject.cs](../MainWindow/MainWindow.LocalProject.cs) |
| Regression checks | [Security tests](../tests/Configurator.Security.Tests/), especially [offline SQL interception](../tests/Configurator.Security.Tests/OfflineSqlProviderInterceptionTests.cs) and [package checks](../tests/Configurator.Security.Tests/ProjectPackageServiceTests.cs) |

Trace a changed screen through its caller and workspace contract. Test changes to the SQL boundary in both modes. For package changes, check validation, save and reopen, and recovery from an interrupted write.
