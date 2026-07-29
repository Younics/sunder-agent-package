using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Storage;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchStore
{
    private readonly object _runtimeEpochLock = new();
    private HistoryRuntimeEpochBinding? _runtimeEpochBinding;

    internal Action<string>? BeforeMutation { get; set; }

    internal void BindRuntimeEpoch(Guid epoch, Action ensureCurrent)
    {
        if (epoch == Guid.Empty)
        {
            throw new ArgumentException("History projection ownership requires a non-empty Runtime epoch.", nameof(epoch));
        }
        ArgumentNullException.ThrowIfNull(ensureCurrent);
        ensureCurrent();

        lock (_runtimeEpochLock)
        {
            _runtimeEpochBinding = new HistoryRuntimeEpochBinding(epoch, ensureCurrent);
        }
        if (_requiresSecureRecreation)
        {
            return;
        }

        lock (_writeLock)
        {
            ensureCurrent();
            using var connection = CreateConnection();
            connection.Open();
            EnableSecureDelete(connection);
            using var transaction = connection.BeginTransaction(deferred: false);
            ensureCurrent();
            string? previousEpoch;
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT RuntimeEpoch FROM HistoryProjectionState WHERE Id = 1;";
                previousEpoch = read.ExecuteScalar() as string;
            }
            using (var claim = connection.CreateCommand())
            {
                claim.Transaction = transaction;
                claim.CommandText = """
                    UPDATE HistoryProjectionState
                    SET RuntimeEpoch = $epoch
                    WHERE Id = 1 AND RuntimeEpoch IS $previousEpoch;
                    """;
                claim.Parameters.AddWithValue("$epoch", epoch.ToString());
                claim.Parameters.AddWithValue("$previousEpoch", (object?)previousEpoch ?? DBNull.Value);
                if (claim.ExecuteNonQuery() != 1)
                {
                    throw new AgentRuntimeGenerationOwnershipLostException(epoch);
                }
            }
            using (var transfer = connection.CreateCommand())
            {
                transfer.Transaction = transaction;
                transfer.CommandText = """
                    UPDATE HistoryProjectionGenerations
                    SET RuntimeEpoch = $epoch
                    WHERE State = 'Active';
                    """;
                transfer.Parameters.AddWithValue("$epoch", epoch.ToString());
                transfer.ExecuteNonQuery();
            }
            ensureCurrent();
            transaction.Commit();
        }
    }

    private SqliteTransaction BeginMutation(SqliteConnection connection, string mutation)
    {
        EnsureCommittedRuntimeEpochCurrent();
        EnableSecureDelete(connection);
        var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            EnsureCommittedRuntimeEpochCurrent();
            EnsureRuntimeEpochCurrent(connection, transaction);
            BeforeMutation?.Invoke(mutation);
            return transaction;
        }
        catch
        {
            transaction.Dispose();
            throw;
        }
    }

    private void CommitMutation(SqliteConnection connection, SqliteTransaction transaction)
    {
        EnsureCommittedRuntimeEpochCurrent();
        EnsureRuntimeEpochCurrent(connection, transaction);
        transaction.Commit();
    }

    private void EnsureCommittedRuntimeEpochCurrent()
        => GetRuntimeEpochBinding()?.EnsureCurrent();

    private void EnsureRuntimeEpochCurrent(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var binding = GetRuntimeEpochBinding();
        if (binding is null)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM HistoryProjectionState
            WHERE Id = 1 AND RuntimeEpoch = $epoch
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$epoch", binding.Epoch.ToString());
        if (command.ExecuteScalar() is null)
        {
            throw new AgentRuntimeGenerationOwnershipLostException(binding.Epoch);
        }
    }

    private void EnsureGenerationRuntimeEpoch(HistoryProjectionGeneration generation)
    {
        var binding = GetRuntimeEpochBinding();
        if (binding is not null && generation.RuntimeEpoch != binding.Epoch)
        {
            throw new AgentRuntimeGenerationOwnershipLostException(binding.Epoch);
        }
    }

    private Guid? GetBoundRuntimeEpoch() => GetRuntimeEpochBinding()?.Epoch;

    private HistoryRuntimeEpochBinding? GetRuntimeEpochBinding()
    {
        lock (_runtimeEpochLock)
        {
            return _runtimeEpochBinding;
        }
    }

    private sealed record HistoryRuntimeEpochBinding(Guid Epoch, Action EnsureCurrent);
}
