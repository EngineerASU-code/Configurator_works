#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Configurator;

/// <summary>Human-readable presentation metadata for existing permissions. This catalog does not grant or revoke rights.</summary>
internal static class PermissionCatalog
{
    internal sealed record Entry(Permission Permission, string Title, string Description, string? Details = null);
    internal sealed record Group(string Title, string Description, IReadOnlyList<Entry> Entries);

    private static Entry E(Permission p, string title, string description, string? details = null) => new(p, title, description, details);

    internal static IReadOnlyList<Group> Groups { get; } = new[]
    {
        new Group("Доступ к режимам", "Видимость навигации и право открыть соответствующий режим.", new[]
        {
            E(Permission.Mode1Show, "Показывать режим 1 — База данных", "Показывает пункт режима в навигации."),
            E(Permission.Mode1View, "Просмотр данных БД", "Разрешает открывать и просматривать записи режима 1."),
            E(Permission.Mode2Show, "Показывать режим 2 — Структура БД", "Показывает режим 2; для входа также нужно право просмотра и хотя бы один доступный подрежим."),
            E(Permission.Mode2GraphShow, "Подрежим: Обзор графа", "Показывает обзор графа в режиме 2."),
            E(Permission.Mode2RelationsShow, "Подрежим: Связи и правила", "Показывает связи и правила в режиме 2."),
            E(Permission.Mode2FieldsShow, "Подрежим: Классы и поля", "Показывает классы и поля в режиме 2."),
            E(Permission.Mode2View, "Просмотр структуры БД", "Разрешает вход в режим 2 для просмотра структуры."),
            E(Permission.Mode3Show, "Показывать режим 3 — Объекты проекта", "Показывает режим 3 в навигации."),
            E(Permission.Mode3View, "Просмотр объектов проекта", "Разрешает открывать и просматривать объекты режима 3."),
            E(Permission.Mode4Show, "Показывать режим 4 — Редактор FB-блоков", "Показывает режим 4 в навигации."),
            E(Permission.Mode4View, "Просмотр режима 4", "Разрешает открыть текущую заготовку режима 4; полноценное редактирование пока не реализовано.")
        }),
        new Group("Данные и записи", "Операции с записями. Добавление, изменение и удаление независимы.", new[]
        {
            E(Permission.Mode1Add, "Режим 1: добавлять строки", "Разрешает добавление записей в режиме 1."),
            E(Permission.Mode1Edit, "Режим 1: изменять строки", "Разрешает редактирование записей в режиме 1."),
            E(Permission.Mode1Delete, "Режим 1: удалять строки", "Разрешает удаление записей в режиме 1."),
            E(Permission.AddData, "Общее право: добавление данных", "Совместимость со старыми ролями; при наличии новых прав режима 1 они имеют приоритет."),
            E(Permission.EditData, "Общее право: изменение данных", "Используется командами редактирования, которые проверяют общее право; не означает право на удаление."),
            E(Permission.DeleteData, "Общее право: удаление данных", "Используется командами удаления, которые проверяют общее право; не означает право на редактирование."),
            E(Permission.ViewData, "Общее право: просмотр данных", "Совместимость со старыми ролями режима 1.")
        }),
        new Group("Структура и конфигурация", "Изменение схемы, справочников, связей и проектных объектов.", new[]
        {
            E(Permission.Mode2Manage, "Режим 2: изменять классы и связи", "Разрешает структурное управление в режиме 2."),
            E(Permission.ManageClasses, "Управление классами", "Разрешает операции управления классами."),
            E(Permission.ManageRelations, "Управление связями", "Разрешает операции управления связями."),
            E(Permission.ManageTextLists, "Управление текстовыми списками", "Разрешает редактирование текстовых справочников."),
            E(Permission.Mode3Manage, "Режим 3: управлять объектами", "Разрешает структурные операции режима 3; само по себе не выдаёт общие права редактирования/удаления записей."),
            E(Permission.ManageObjects, "Управление объектами", "Общее/legacy-разрешение на управление объектами."),
            E(Permission.ManageDatabases, "Управление базами данных", "Разрешает административные операции с подключёнными базами."),
            E(Permission.Mode4Manage, "Режим 4: управление FB-блоками", "Право зарезервировано для будущей реализации; текущий режим является заготовкой.")
        }),
        new Group("Инструменты и обслуживание", "Отдельные инструменты и операции обслуживания данных.", new[]
        {
            E(Permission.CheckTools, "Запуск инструментов проверок", "Открывает четыре инструмента проверок в меню «Опции».", "Включает проверку свободных полей, свободных адресов, дублирования адресов и дублирования записей. Для запуска требуется доступная подключённая Online-БД. Это право не выдаёт свободный SQL, экспорт или права на изменение данных."),
            E(Permission.FreeSql, "Свободный SQL", "Разрешает открыть инструмент свободного SQL. Это отдельное право, не включённое в «Проверки»."),
            E(Permission.ExportData, "Экспорт данных и проекта", "Разрешает команды экспорта, где это право проверяется."),
            E(Permission.RestoreJournal, "Восстановление из журнала", "Разрешает операции восстановления, где это право проверяется.")
        }),
        new Group("Администрирование", "Пользователи и административные операции.", new[]
        {
            E(Permission.ManageUsers, "Управление пользователями", "Разрешение модели для управления учётными записями и ролями; вход в окно также ограничен административной сессией.")
        })
    };

    internal static IReadOnlyList<Entry> AllEntries => Groups.SelectMany(x => x.Entries).ToArray();
    internal static Entry Get(Permission permission) => AllEntries.FirstOrDefault(x => x.Permission == permission)
        ?? new Entry(permission, permission.ToString(), "Нет дополнительного описания для этого разрешения.");
}
