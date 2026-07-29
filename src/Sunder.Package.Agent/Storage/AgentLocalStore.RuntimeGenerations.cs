using Microsoft.Data.Sqlite;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private readonly object _runtimeGenerationSync = new();
    private Guid? _ownedRuntimeGeneration;
    private Guid _runtimeGenerationFenceToken;
    private bool _runtimeGenerationFenced;

    internal Guid? OwnedRuntimeGeneration
    {
        get
        {
            lock (_runtimeGenerationSync)
            {
                return _ownedRuntimeGeneration;
            }
        }
    }

    internal void BindRuntimeGeneration(Guid epoch, Guid fenceToken)
    {
        if (epoch == Guid.Empty || fenceToken == Guid.Empty)
        {
            throw new ArgumentException("Runtime generation ownership requires non-empty epoch and fence tokens.");
        }

        lock (_runtimeGenerationSync)
        {
            if (_ownedRuntimeGeneration is { } current
                && (current != epoch || _runtimeGenerationFenceToken != fenceToken))
            {
                throw new InvalidOperationException("The Agent store is already bound to another Runtime generation.");
            }
            _ownedRuntimeGeneration = epoch;
            _runtimeGenerationFenceToken = fenceToken;
            _runtimeGenerationFenced = false;
        }
    }

    internal bool FenceRuntimeGeneration(Guid epoch, Guid fenceToken)
    {
        lock (_runtimeGenerationSync)
        {
            if (_ownedRuntimeGeneration != epoch || _runtimeGenerationFenceToken != fenceToken)
            {
                return false;
            }

            _runtimeGenerationFenced = true;
            return true;
        }
    }

    internal bool IsRuntimeGenerationFenced(Guid epoch)
    {
        lock (_runtimeGenerationSync)
        {
            return _ownedRuntimeGeneration == epoch && _runtimeGenerationFenced;
        }
    }

    internal void ClearRuntimeGeneration(Guid epoch, Guid fenceToken)
    {
        lock (_runtimeGenerationSync)
        {
            if (_ownedRuntimeGeneration == epoch && _runtimeGenerationFenceToken == fenceToken)
            {
                _ownedRuntimeGeneration = null;
                _runtimeGenerationFenceToken = Guid.Empty;
                _runtimeGenerationFenced = false;
            }
        }
    }

    internal void EnsureRuntimeGenerationCurrent()
    {
        using var connection = CreateConnection();
        connection.Open();
        EnsureRuntimeGenerationCurrent(connection, transaction: null);
    }

    internal AgentRuntimeGenerationOwnership? GetCurrentRuntimeGeneration()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT generation.Epoch,
                   generation.RuntimeSessionGeneration,
                   generation.ProcessId,
                   generation.ProcessStartedAtUtc,
                   generation.Status,
                   generation.LeaseExpiresAtUtc
            FROM AgentRuntimeGenerationState state
            INNER JOIN AgentRuntimeGenerations generation
                ON generation.Epoch = state.CurrentEpoch
            WHERE state.SingletonId = 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AgentRuntimeGenerationOwnership(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetInt32(2),
                DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    internal bool TryCommitRuntimeGeneration(
        PackageRuntimeGeneration generation,
        AgentRuntimeProcessIdentity process,
        Guid? expectedCurrentEpoch,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAtUtc)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = GetCurrentRuntimeGeneration(connection, transaction);
        if (current?.Epoch != expectedCurrentEpoch
            || current is { Status: "Committed" }
               && current.Epoch != generation.ActivationId
               && current.LeaseExpiresAtUtc > now)
        {
            transaction.Rollback();
            return false;
        }

        if (current is { Status: "Committed" } && current.Epoch != generation.ActivationId)
        {
            using var expire = connection.CreateCommand();
            expire.Transaction = transaction;
            expire.CommandText = """
                UPDATE AgentRuntimeGenerations
                SET Status = 'Expired',
                    StoppedAtUtc = $now
                WHERE Epoch = $epoch
                  AND Status = 'Committed'
                  AND LeaseExpiresAtUtc <= $now;
                """;
            expire.Parameters.AddWithValue("$now", now.ToString("O"));
            expire.Parameters.AddWithValue("$epoch", current.Epoch.ToString());
            if (expire.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO AgentRuntimeGenerations (
                    Epoch, RuntimeSessionGeneration, ProcessId, ProcessStartedAtUtc,
                    Status, CreatedAtUtc, CommittedAtUtc, LastHeartbeatAtUtc,
                    LeaseExpiresAtUtc, StoppedAtUtc)
                VALUES (
                    $epoch, $runtimeSessionGeneration, $processId, $processStartedAtUtc,
                    'Committed', $now, $now, $now, $leaseExpiresAtUtc, NULL)
                ON CONFLICT(Epoch) DO UPDATE SET
                    RuntimeSessionGeneration = excluded.RuntimeSessionGeneration,
                    ProcessId = excluded.ProcessId,
                    ProcessStartedAtUtc = excluded.ProcessStartedAtUtc,
                    Status = 'Committed',
                    CommittedAtUtc = excluded.CommittedAtUtc,
                    LastHeartbeatAtUtc = excluded.LastHeartbeatAtUtc,
                    LeaseExpiresAtUtc = excluded.LeaseExpiresAtUtc,
                    StoppedAtUtc = NULL;
                """;
            upsert.Parameters.AddWithValue("$epoch", generation.ActivationId.ToString());
            upsert.Parameters.AddWithValue("$runtimeSessionGeneration", generation.SessionGeneration);
            upsert.Parameters.AddWithValue("$processId", process.ProcessId);
            upsert.Parameters.AddWithValue("$processStartedAtUtc", process.StartedAtUtc.ToString("O"));
            upsert.Parameters.AddWithValue("$now", now.ToString("O"));
            upsert.Parameters.AddWithValue("$leaseExpiresAtUtc", leaseExpiresAtUtc.ToString("O"));
            upsert.ExecuteNonQuery();
        }

        using (var publish = connection.CreateCommand())
        {
            publish.Transaction = transaction;
            publish.CommandText = "UPDATE AgentRuntimeGenerationState SET CurrentEpoch = $epoch WHERE SingletonId = 1;";
            publish.Parameters.AddWithValue("$epoch", generation.ActivationId.ToString());
            if (publish.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The Agent Runtime generation state row is missing.");
            }
        }

        transaction.Commit();
        return true;
    }

    internal bool TryMarkRuntimeGenerationDead(
        AgentRuntimeGenerationOwnership expected,
        DateTimeOffset now)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AgentRuntimeGenerations
            SET Status = 'Dead',
                StoppedAtUtc = $now
            WHERE Epoch = $epoch
              AND ProcessId = $processId
              AND ProcessStartedAtUtc = $processStartedAtUtc
              AND Status = 'Committed'
              AND EXISTS (
                  SELECT 1
                  FROM AgentRuntimeGenerationState state
                  WHERE state.SingletonId = 1
                    AND state.CurrentEpoch = AgentRuntimeGenerations.Epoch);
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$epoch", expected.Epoch.ToString());
        command.Parameters.AddWithValue("$processId", expected.ProcessId);
        command.Parameters.AddWithValue("$processStartedAtUtc", expected.ProcessStartedAtUtc.ToString("O"));
        return command.ExecuteNonQuery() == 1;
    }

    internal bool RenewRuntimeGeneration(
        Guid epoch,
        Guid fenceToken,
        DateTimeOffset expectedLeaseExpiresAtUtc,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset renewalDeadlineUtc,
        TimeProvider timeProvider,
        int commandTimeoutSeconds,
        Action? commandStarting = null)
    {
        if (!IsRuntimeGenerationFenceCurrent(epoch, fenceToken))
        {
            return false;
        }

        using var connection = CreateConnection();
        connection.DefaultTimeout = commandTimeoutSeconds;
        connection.Open();
        connection.CreateFunction<string, string, long>(
            "sunder_runtime_generation_fence_current",
            (epochValue, fenceTokenValue) =>
                Guid.TryParse(epochValue, out var candidateEpoch)
                && Guid.TryParse(fenceTokenValue, out var candidateFenceToken)
                && IsRuntimeGenerationFenceCurrent(candidateEpoch, candidateFenceToken)
                    ? 1
                    : 0);
        connection.CreateFunction(
            "sunder_runtime_generation_now_utc_ticks",
            () => timeProvider.GetUtcNow().UtcTicks);
        using var command = connection.CreateCommand();
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = """
            UPDATE AgentRuntimeGenerations
            SET LastHeartbeatAtUtc = $now,
                LeaseExpiresAtUtc = $leaseExpiresAtUtc
            WHERE Epoch = $epoch
              AND Status = 'Committed'
              AND LeaseExpiresAtUtc = $expectedLeaseExpiresAtUtc
              AND sunder_runtime_generation_fence_current($epoch, $fenceToken) = 1
              AND sunder_runtime_generation_now_utc_ticks() < $renewalDeadlineUtcTicks
              AND EXISTS (
                  SELECT 1
                  FROM AgentRuntimeGenerationState state
                  WHERE state.SingletonId = 1
                    AND state.CurrentEpoch = AgentRuntimeGenerations.Epoch);
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$leaseExpiresAtUtc", leaseExpiresAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$expectedLeaseExpiresAtUtc", expectedLeaseExpiresAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$renewalDeadlineUtcTicks", renewalDeadlineUtc.UtcTicks);
        command.Parameters.AddWithValue("$epoch", epoch.ToString());
        command.Parameters.AddWithValue("$fenceToken", fenceToken.ToString());
        commandStarting?.Invoke();
        return command.ExecuteNonQuery() == 1;
    }

    private void EnsureRuntimeGenerationCurrent(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var binding = GetRuntimeGenerationBinding();
        if (binding is null)
        {
            return;
        }
        if (binding.Fenced)
        {
            throw new AgentRuntimeGenerationOwnershipLostException(binding.Epoch);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM AgentRuntimeGenerationState state
            INNER JOIN AgentRuntimeGenerations generation
                ON generation.Epoch = state.CurrentEpoch
            WHERE state.SingletonId = 1
              AND generation.Epoch = $epoch
              AND generation.Status = 'Committed'
              AND generation.LeaseExpiresAtUtc > $now
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$epoch", binding.Epoch.ToString());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        if (command.ExecuteScalar() is null
            || !IsRuntimeGenerationFenceCurrent(binding.Epoch, binding.FenceToken))
        {
            throw new AgentRuntimeGenerationOwnershipLostException(binding.Epoch);
        }
    }

    private RuntimeGenerationBinding? GetRuntimeGenerationBinding()
    {
        lock (_runtimeGenerationSync)
        {
            return _ownedRuntimeGeneration is { } epoch
                ? new RuntimeGenerationBinding(epoch, _runtimeGenerationFenceToken, _runtimeGenerationFenced)
                : null;
        }
    }

    private bool IsRuntimeGenerationFenceCurrent(Guid epoch, Guid fenceToken)
    {
        lock (_runtimeGenerationSync)
        {
            return _ownedRuntimeGeneration == epoch
                   && _runtimeGenerationFenceToken == fenceToken
                   && !_runtimeGenerationFenced;
        }
    }

    private bool IsRuntimeGenerationBindingCurrent(Guid epoch, Guid fenceToken)
    {
        lock (_runtimeGenerationSync)
        {
            return _ownedRuntimeGeneration == epoch && _runtimeGenerationFenceToken == fenceToken;
        }
    }

    internal void StopRuntimeGeneration(Guid epoch, DateTimeOffset now)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AgentRuntimeGenerations
            SET Status = 'Stopped',
                StoppedAtUtc = $now,
                LeaseExpiresAtUtc = $now
            WHERE Epoch = $epoch
              AND Status = 'Committed'
              AND EXISTS (
                  SELECT 1
                  FROM AgentRuntimeGenerationState state
                  WHERE state.SingletonId = 1
                    AND state.CurrentEpoch = AgentRuntimeGenerations.Epoch);
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$epoch", epoch.ToString());
        command.ExecuteNonQuery();
    }

    internal void StopRuntimeGeneration(Guid epoch, Guid fenceToken, DateTimeOffset now)
    {
        if (IsRuntimeGenerationBindingCurrent(epoch, fenceToken))
        {
            StopRuntimeGeneration(epoch, now);
        }
    }

    private static AgentRuntimeGenerationOwnership? GetCurrentRuntimeGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT generation.Epoch,
                   generation.RuntimeSessionGeneration,
                   generation.ProcessId,
                   generation.ProcessStartedAtUtc,
                   generation.Status,
                   generation.LeaseExpiresAtUtc
            FROM AgentRuntimeGenerationState state
            INNER JOIN AgentRuntimeGenerations generation
                ON generation.Epoch = state.CurrentEpoch
            WHERE state.SingletonId = 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AgentRuntimeGenerationOwnership(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetInt32(2),
                DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }
}

internal sealed record RuntimeGenerationBinding(Guid Epoch, Guid FenceToken, bool Fenced);

internal sealed record AgentRuntimeGenerationOwnership(
    Guid Epoch,
    long RuntimeSessionGeneration,
    int ProcessId,
    DateTimeOffset ProcessStartedAtUtc,
    string Status,
    DateTimeOffset LeaseExpiresAtUtc);

internal sealed record AgentRuntimeProcessIdentity(int ProcessId, DateTimeOffset StartedAtUtc);

internal sealed class AgentRuntimeGenerationOwnershipLostException(Guid epoch)
    : InvalidOperationException($"Agent Runtime generation '{epoch}' no longer owns the durable generation lease.")
{
    internal Guid Epoch { get; } = epoch;
}

internal sealed class AgentRuntimeGenerationHeartbeatException(Guid epoch, Exception innerException)
    : InvalidOperationException(
        $"Agent Runtime generation '{epoch}' could not renew its durable lease before the safety deadline.",
        innerException)
{
    internal Guid Epoch { get; } = epoch;
}
