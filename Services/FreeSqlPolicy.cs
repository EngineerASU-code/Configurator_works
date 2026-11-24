using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Configurator;

/// <summary>
/// Fail-closed policy for the interactive SQL editor. This is an application guard,
/// not a replacement for least-privilege SQL Server permissions.
/// </summary>
public static class FreeSqlPolicy
{
    public static void Validate(string sql) => ValidateAndGetReturnsRows(sql);

    public static bool ValidateAndGetReturnsRows(string sql)
        => Analyze(sql).Table == null;

    internal static (string Schema, string Table) Analyze(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException("SQL-запрос пуст.");

        TSqlFragment fragment;
        IList<ParseError> errors;
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using (var reader = new StringReader(sql))
            fragment = parser.Parse(reader, out errors);

        if (errors.Count > 0 || fragment is not TSqlScript script)
            throw new InvalidOperationException("SQL не прошёл разбор. Запрос отклонён без выполнения.");

        var statements = script.Batches.SelectMany(batch => batch.Statements).ToList();
        if (statements.Count != 1)
            throw new InvalidOperationException("Разрешён только один SQL-оператор за выполнение.");

        // Preserve useful data operations, but don't let this editor execute DDL,
        // transaction control, context changes, procedural code or server commands.
        if (statements[0] is not (SelectStatement or InsertStatement or UpdateStatement or DeleteStatement))
            throw new InvalidOperationException(
                "Разрешены только одиночные SELECT, INSERT, UPDATE и DELETE. " +
                "DDL, USE, EXEC/процедуры и административные команды запрещены.");

        // Protection from accidental mass changes: UPDATE/DELETE must carry an explicit WHERE.
        // A deliberate full-table operation is still possible with "WHERE 1 = 1".
        if (statements[0] is UpdateStatement { UpdateSpecification.WhereClause: null })
            throw new InvalidOperationException(
                "UPDATE без WHERE запрещён. Добавьте условие (для сознательного изменения всех строк — WHERE 1 = 1).");
        if (statements[0] is DeleteStatement { DeleteSpecification.WhereClause: null })
            throw new InvalidOperationException(
                "DELETE без WHERE запрещён. Добавьте условие (для сознательного удаления всех строк — WHERE 1 = 1).");

        // ScriptDom's visitor API has no ExplicitVisit(TSqlFragment) overload.
        // Walk the parsed AST by reflection as a defensive backstop so node types
        // such as BulkOpenRowsetTableReference cannot evade the specific visitors.
        var boundaryError = FindBoundaryViolation(fragment, new HashSet<TSqlFragment>());
        if (boundaryError != null)
            throw new InvalidOperationException(boundaryError);

        var visitor = new BoundaryVisitor();
        fragment.Accept(visitor);
        if (visitor.Error != null)
            throw new InvalidOperationException(visitor.Error);

        DataModificationSpecification modification = statements[0] switch
        {
            InsertStatement insert => insert.InsertSpecification,
            UpdateStatement update => update.UpdateSpecification,
            DeleteStatement delete => delete.DeleteSpecification,
            _ => null
        };
        if (modification == null) return (null, null);
        if (modification.Target is not NamedTableReference target ||
            target.SchemaObject.Identifiers.Count != 2 || modification.OutputIntoClause != null ||
            modification is UpdateDeleteSpecificationBase { FromClause: not null })
            throw new InvalidOperationException("Для записи укажите одну таблицу в виде схема.таблица без косвенных целей.");
        string schema = target.SchemaObject.SchemaIdentifier.Value;
        string table = target.SchemaObject.BaseIdentifier.Value;
        Configurator.Core.Projects.ProjectWriteGuard.EnsureDataTableWritable(schema + "." + table);
        return (schema, table);
    }

    private static string FindBoundaryViolation(TSqlFragment fragment, HashSet<TSqlFragment> visited)
    {
        if (!visited.Add(fragment))
            return null;

        var typeName = fragment.GetType().Name;
        if (typeName.Contains("OpenRowset", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("OpenQuery", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("BulkOpenRowset", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("AdHocTableReference", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("AdHocDataSource", StringComparison.OrdinalIgnoreCase))
            return "OPENROWSET/OPENQUERY/OPENDATASOURCE и внешние источники запрещены в свободном SQL.";

        foreach (var property in fragment.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod == null)
                continue;

            object value;
            try { value = property.GetValue(fragment); }
            catch { continue; }

            if (value is TSqlFragment child)
            {
                var error = FindBoundaryViolation(child, visited);
                if (error != null) return error;
            }
            else if (value is System.Collections.IEnumerable items && value is not string)
            {
                foreach (var item in items)
                {
                    if (item is TSqlFragment childItem)
                    {
                        var error = FindBoundaryViolation(childItem, visited);
                        if (error != null) return error;
                    }
                }
            }
        }

        return null;
    }

    private sealed class BoundaryVisitor : TSqlFragmentVisitor
    {
        public string Error { get; private set; }

        public override void ExplicitVisit(DataModificationTableReference node) =>
            Error ??= "Вложенное изменение данных запрещено.";

        public override void ExplicitVisit(NextValueForExpression node) =>
            Error ??= "Изменение счётчика последовательности в свободном SQL запрещено.";

        public override void ExplicitVisit(NamedTableReference node)
        {
            if (node.SchemaObject?.Identifiers?.Count > 2)
                Error ??= "Обращение к другой базе/серверу через составное имя запрещено.";
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            if (node.Into != null)
                Error ??= "SELECT INTO запрещён: он создаёт объекты вне обычного режима чтения.";
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecuteInsertSource node)
        {
            Error ??= "INSERT ... EXEC запрещён.";
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(OpenQueryTableReference node)
        {
            Error ??= "OPENQUERY запрещён в свободном SQL.";
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AdHocTableReference node)
        {
            Error ??= "OPENDATASOURCE запрещён в свободном SQL.";
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(OpenRowsetTableReference node)
        {
            Error ??= "OPENROWSET запрещён в свободном SQL.";
            base.ExplicitVisit(node);
        }
    }
}
