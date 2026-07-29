namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal const int MaxSessionCleanupJobsPerPass = 256;

    internal event Action? SessionCleanupJobsChanged;

    internal void RegisterSessionDataCleaners(
        IReadOnlyList<AgentSessionDataCleanerIdentity> cleaners,
        DateTimeOffset now)
    {
        if (cleaners.Count == 0)
        {
            return;
        }

        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        RegisterSessionDataCleaners(connection, transaction, cleaners, now);
        transaction.Commit();
    }

    internal AgentSessionCleanupJobClaim? TryClaimSessionCleanupJob(
        AgentSessionDataCleanerIdentity cleaner,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        long jobId;
        Guid sessionId;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT JobId, SessionId
                FROM AgentSessionCleanupJobs
                WHERE PackageId = $packageId
                  AND CleanerId = $cleanerId
                  AND ((Status = 'Pending' AND NextAttemptAtUtc <= $now)
                       OR (Status = 'InFlight'
                           AND (LeaseExpiresAtUtc IS NULL OR LeaseExpiresAtUtc <= $now)))
                ORDER BY JobId
                LIMIT 1;
                """;
            BindCleanerIdentity(select, cleaner);
            select.Parameters.AddWithValue("$now", now.ToString("O"));
            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Commit();
                return null;
            }

            jobId = reader.GetInt64(0);
            sessionId = Guid.Parse(reader.GetString(1));
        }

        var leaseToken = Guid.NewGuid().ToString("N");
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE AgentSessionCleanupJobs
                SET Status = 'InFlight',
                    LeaseToken = $leaseToken,
                    LeaseExpiresAtUtc = $leaseExpiresAtUtc,
                    LastAttemptAtUtc = $now,
                    UpdatedAtUtc = $now
                WHERE JobId = $jobId
                  AND ((Status = 'Pending' AND NextAttemptAtUtc <= $now)
                       OR (Status = 'InFlight'
                           AND (LeaseExpiresAtUtc IS NULL OR LeaseExpiresAtUtc <= $now)));
                """;
            update.Parameters.AddWithValue("$leaseToken", leaseToken);
            update.Parameters.AddWithValue("$leaseExpiresAtUtc", now.Add(leaseDuration).ToString("O"));
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$jobId", jobId);
            if (update.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        transaction.Commit();
        return new AgentSessionCleanupJobClaim(
            jobId,
            cleaner.PackageId,
            cleaner.CleanerId,
            sessionId,
            leaseToken);
    }

    internal bool CompleteSessionCleanupJob(AgentSessionCleanupJobClaim claim, DateTimeOffset now)
        => UpdateClaimedSessionCleanupJob(
            claim,
            """
            Status = 'Completed',
            LeaseToken = NULL,
            LeaseExpiresAtUtc = NULL,
            CompletedAtUtc = $now,
            LastError = NULL,
            UpdatedAtUtc = $now
            """,
            now);

    internal bool ReleaseSessionCleanupJob(AgentSessionCleanupJobClaim claim, DateTimeOffset now)
        => UpdateClaimedSessionCleanupJob(
            claim,
            """
            Status = 'Pending',
            NextAttemptAtUtc = $now,
            LeaseToken = NULL,
            LeaseExpiresAtUtc = NULL,
            UpdatedAtUtc = $now
            """,
            now);

    internal bool FailSessionCleanupJob(
        AgentSessionCleanupJobClaim claim,
        Exception exception,
        DateTimeOffset now)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        int attemptCount;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT AttemptCount
                FROM AgentSessionCleanupJobs
                WHERE JobId = $jobId AND Status = 'InFlight' AND LeaseToken = $leaseToken;
                """;
            BindCleanupClaim(select, claim);
            var value = select.ExecuteScalar();
            if (value is null)
            {
                transaction.Rollback();
                return false;
            }
            attemptCount = Convert.ToInt32(value) + 1;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE AgentSessionCleanupJobs
                SET Status = 'Pending',
                    AttemptCount = $attemptCount,
                    NextAttemptAtUtc = $nextAttemptAtUtc,
                    LeaseToken = NULL,
                    LeaseExpiresAtUtc = NULL,
                    LastError = $lastError,
                    UpdatedAtUtc = $now
                WHERE JobId = $jobId AND Status = 'InFlight' AND LeaseToken = $leaseToken;
                """;
            update.Parameters.AddWithValue("$attemptCount", attemptCount);
            update.Parameters.AddWithValue(
                "$nextAttemptAtUtc",
                now.Add(CalculateLifecycleRetryDelay(attemptCount)).ToString("O"));
            update.Parameters.AddWithValue(
                "$lastError",
                $"cleanup_callback_failure ({GetLifecycleExceptionType(exception)})");
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            BindCleanupClaim(update, claim);
            if (update.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        transaction.Commit();
        SignalSessionCleanupJobsChanged();
        return true;
    }

    internal int RecoverSessionCleanupJobLeases(DateTimeOffset now, int limit = MaxSessionCleanupJobsPerPass)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentSessionCleanupJobs
            SET Status = 'Pending',
                NextAttemptAtUtc = $now,
                LeaseToken = NULL,
                LeaseExpiresAtUtc = NULL,
                UpdatedAtUtc = $now
            WHERE JobId IN (
                SELECT JobId
                FROM AgentSessionCleanupJobs
                WHERE Status = 'InFlight'
                ORDER BY JobId
                LIMIT $limit);
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxSessionCleanupJobsPerPass));
        var recovered = command.ExecuteNonQuery();
        transaction.Commit();
        if (recovered > 0)
        {
            SignalSessionCleanupJobsChanged();
        }
        return recovered;
    }

    internal IReadOnlyList<AgentSessionCleanupJobState> ListSessionCleanupJobs()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT JobId, PackageId, CleanerId, SessionId, Status, AttemptCount,
                   NextAttemptAtUtc, LastError
            FROM AgentSessionCleanupJobs
            ORDER BY JobId;
            """;
        using var reader = command.ExecuteReader();
        var jobs = new List<AgentSessionCleanupJobState>();
        while (reader.Read())
        {
            jobs.Add(new AgentSessionCleanupJobState(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                reader.GetInt32(5),
                DateTimeOffset.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return jobs;
    }

    private static void EnqueueSessionCleanupJobs(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        IReadOnlyList<Guid> deletedSessionIds,
        IReadOnlyList<AgentSessionDataCleanerIdentity> activeCleaners,
        DateTimeOffset now)
    {
        RegisterSessionDataCleaners(connection, transaction, activeCleaners, now);
        foreach (var sessionId in deletedSessionIds.Distinct())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO AgentSessionCleanupJobs (
                    PackageId, CleanerId, SessionId, Status, AttemptCount, NextAttemptAtUtc,
                    CreatedAtUtc, UpdatedAtUtc)
                SELECT PackageId, CleanerId, $sessionId, 'Pending', 0, $now, $now, $now
                FROM AgentSessionDataCleaners;
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    private static void RegisterSessionDataCleaners(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        IReadOnlyList<AgentSessionDataCleanerIdentity> cleaners,
        DateTimeOffset now)
    {
        foreach (var cleaner in cleaners.Distinct())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO AgentSessionDataCleaners (PackageId, CleanerId, LastSeenAtUtc)
                VALUES ($packageId, $cleanerId, $now)
                ON CONFLICT(PackageId, CleanerId) DO UPDATE SET LastSeenAtUtc = excluded.LastSeenAtUtc;
                """;
            BindCleanerIdentity(command, cleaner);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    private bool UpdateClaimedSessionCleanupJob(
        AgentSessionCleanupJobClaim claim,
        string assignments,
        DateTimeOffset now)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE AgentSessionCleanupJobs
            SET {assignments}
            WHERE JobId = $jobId AND Status = 'InFlight' AND LeaseToken = $leaseToken;
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        BindCleanupClaim(command, claim);
        var updated = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        if (updated)
        {
            SignalSessionCleanupJobsChanged();
        }
        return updated;
    }

    private static void BindCleanerIdentity(
        Microsoft.Data.Sqlite.SqliteCommand command,
        AgentSessionDataCleanerIdentity cleaner)
    {
        command.Parameters.AddWithValue("$packageId", cleaner.PackageId);
        command.Parameters.AddWithValue("$cleanerId", cleaner.CleanerId);
    }

    private static void BindCleanupClaim(
        Microsoft.Data.Sqlite.SqliteCommand command,
        AgentSessionCleanupJobClaim claim)
    {
        command.Parameters.AddWithValue("$jobId", claim.JobId);
        command.Parameters.AddWithValue("$leaseToken", claim.LeaseToken);
    }

    private void SignalSessionCleanupJobsChanged()
    {
        var handlers = SessionCleanupJobsChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch
            {
                // A wake hint must not affect the source transaction.
            }
        }
    }
}
