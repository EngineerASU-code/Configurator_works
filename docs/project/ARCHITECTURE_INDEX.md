# Architecture index

Краткая навигационная карта Configurator по Graphify Cloud и локальным исходникам.

## Область и свежесть

- Репозиторий: `EngineerASU-code/Configurator_works` (`origin` — GitHub), локальная ветка `main`.
- Исходный снимок Graphify: `94bac252289d73d86ef1fce542c56dacaa544a3c`, индекс построен `2026-11-16 14:20 UTC`. Это историческая отметка проверки, а не утверждение о текущем HEAD.
- Граф не подтверждает последующие локальные изменения Matrix/AWL, Program, превью объектов и подготовки проекта. Для них сверяй текущие исходники и [отчёт подготовки](PROJECT_PREPARATION_2026-11-17.md). Автоматическое обновление графа после публикации здесь не проверялось.
- Размер графа: 3 894 узла и 10 088 связей. Репозиторий доступен для запросов; на текущем плане Graphify capability `patterns` недоступна, а из проектных документов индексируется только `README.md`.

## Основные слои и связи

- **Запуск и WPF-оболочка:** `App.xaml.cs` применяет тему и инициализирует авторизацию, при необходимости показывает первоначальную настройку, затем создаёт `MainWindow`. `MainWindow/` — оболочка и переключатель рабочих режимов; код окна разделён на partial-файлы.
- **Экраны:** `Modes/` содержит основные режимы и специализированные окна, `FirstScreen/` — элементы и диалоги стартового экрана.
- **Core:** `Core/Architecture/` содержит модели архитектуры и правила. `Core/Projects/` задаёт модели проекта, контракты источников и `IProjectWorkspace` — storage-neutral контракт чтения схемы и страниц данных, CRUD и архитектурных метаданных.
- **Выбор источника проекта:** `ProjectDataSourceContext` управляет жизненным циклом активных reader/writer-адаптеров и собирает `ProjectWorkspace`, реализующий `IProjectWorkspace`. В Online используются `OnlineProjectDataSource` и SQL CRUD-адаптер; в Offline — `OfflineProjectDataSource` с локальным снимком проекта.
- **Граница SQL:** переключение контекста сообщает Offline-состояние в `SqlProviderGuard`. SQL-соединения в Online создаются и открываются через guard; в Offline новые открытия блокируются.
- **Локальные пакеты:** `Infrastructure/Projects/ProjectPackageService.cs` открывает, проверяет и сохраняет `.cfgproj`, включая восстановление после прерванной записи. `MainWindow/MainWindow.LocalProject.cs` связывает этот сервис с действиями пользователя и локальным workspace.
- **Архитектура SQL:** `Services/DatabaseArchitectureAnalyzer.cs` читает схему БД и строит проектные связи. `Infrastructure/Database/ClassArchitectureReader.cs` получает поля через SQL-адаптер или источник локального проекта. `Application/Architecture/ClassArchitectureService.cs` управляет каталогом определений классов.

## Точки входа для навигации

- Matrix и AWL: `Modes/MatrixCatalogControl.xaml.cs`, `Services/ProgramSupportRepository.cs`, `Services/MatrixAwlExporter.cs`; разделение таблиц — `Models/ProgramSupportModels.cs`.
- Иерархия Program: `Modes/ProgramManagementControl.xaml.cs`, `Modes/ProjectModeView.ProgramTools.cs`, `Services/ProgramRepository/`.
- Превью типов объектов: `Core/Architecture/ObjectTypePreviewCatalog.cs`, `Modes/ProjectModeView.ObjectPreviews.cs`, `Images/ObjectPreviews/`.

- Запуск и главное окно: `App.xaml.cs`, `MainWindow/MainWindow.xaml.cs`.
- Переключение Online/Offline и открытие локального проекта: `MainWindow/MainWindow.LocalProject.cs`, `Core/Projects/ProjectDataSourceContext.cs`.
- Контракты и единый workspace: `Core/Projects/IProjectWorkspace.cs`, `ProjectWorkspace.cs`, `IProjectDataSource.cs`.
- Источники Online/Offline и блокировка SQL в Offline: `OnlineProjectDataSource.cs`, `OfflineProjectDataSource.cs`, `SqlProviderGuard.cs`.
- Сохранение и восстановление пакета: `Infrastructure/Projects/ProjectPackageService.cs`.
- Чтение SQL-схемы и моделей архитектуры: `Services/DatabaseArchitectureAnalyzer.cs`, `Infrastructure/Database/ClassArchitectureReader.cs`, `Application/Architecture/ClassArchitectureService.cs`.

## Связанные тесты и уверенность

В `tests/Configurator.Security.Tests/` Graphify связывает workspace и переключение источников с `ProjectWorkspaceTests.cs`, `ProjectDataSourceContextTests.cs`, `OfflineProjectWorkflowIntegrationTests.cs` и `OfflineSqlProviderInterceptionTests.cs`. Для локальных данных и пакетов смотри `OfflineProjectDataSourceTests.cs`, `ProjectPackageServiceTests.cs` и `ProjectPackageAdversarialTests.cs`; права доступа проверяются в `PermissionMatrixSecurityTests.cs`. Это навигационные связи графа, а не полный отчёт о тестовом покрытии.

Уверенность высокая для запуска, контрактов workspace, переключения Online/Offline, SQL-границы и пакета: связи Graphify сверены с локальными исходниками. Карта не перечисляет каждый экран и не заменяет проверку исходников, особенно в файлах с локальными изменениями.
