#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Configurator.Core.Projects;

/// <summary>
/// Last-line SQL isolation boundary. Connection creation and opening are serialized
/// against Online/Offline transitions. A transition prevents any not-yet-opened
/// connection from opening after Offline activation; queries already running on an
/// open connection are allowed to finish.
/// </summary>
public static class SqlProviderGuard
{
    private static readonly SemaphoreSlim TransitionGate = new(1, 1);
    private static int _offlineActive;

    public static bool IsOfflineActive => Volatile.Read(ref _offlineActive) == 1;

    public static void SetOfflineActive(bool isOffline)
    {
        TransitionGate.Wait();
        try { Volatile.Write(ref _offlineActive, isOffline ? 1 : 0); }
        finally { TransitionGate.Release(); }
    }

    public static void EnsureSqlAllowed(string? operation = null)
    {
        if (IsOfflineActive)
            throw new InvalidOperationException(
                "Обращение к SQL Server запрещено в Offline-режиме" +
                (string.IsNullOrWhiteSpace(operation) ? "." : $": {operation}.") +
                " Используйте источник данных текущего локального проекта.");
    }

    public static SqlConnection CreateConnection(string connectionString)
    {
        TransitionGate.Wait();
        try
        {
            EnsureSqlAllowed("создание SqlConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Строка подключения не задана.", nameof(connectionString));
            return new SqlConnection(connectionString);
        }
        finally { TransitionGate.Release(); }
    }

    /// <summary>
    /// Opens a guarded connection atomically with respect to a mode transition.
    /// If Offline wins the gate first, the connection is never opened. If opening
    /// wins first, the transition waits for OpenAsync to finish and then blocks
    /// every subsequent open. The already-open query may finish normally.
    /// </summary>
    public static async Task OpenConnectionAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await TransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSqlAllowed("connection.OpenAsync");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { TransitionGate.Release(); }
    }

    /// <summary>Synchronous counterpart for the few legacy synchronous SQL paths.</summary>
    public static void OpenConnection(SqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        TransitionGate.Wait();
        try
        {
            EnsureSqlAllowed("connection.Open");
            connection.Open();
        }
        finally { TransitionGate.Release(); }
    }
}
