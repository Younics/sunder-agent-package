using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    public IReadOnlyList<AgentProfileRecord> ListProfiles()
    {
        using var connection = CreateConnection();
        connection.Open();
        return ListProfiles(connection);
    }

    public AgentProfileRecord? GetProfile(string profileId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProfileId, DisplayName, Description, Instructions, ProviderId, ModelId, EmbeddingProviderId, EmbeddingModelId, CreatedAtUtc, UpdatedAtUtc, BehaviorLoopId, BehaviorLoopSourceId, BehaviorLoopSettingsJson, IsInternal FROM AgentProfiles WHERE ProfileId = $id;";
        command.Parameters.AddWithValue("$id", profileId);

        string loadedProfileId;
        string displayName;
        string? description;
        string? instructions;
        string? chatProviderId;
        string? chatModelId;
        string? embeddingProviderId;
        string? embeddingModelId;
        DateTimeOffset createdAtUtc;
        DateTimeOffset updatedAtUtc;
        string? behaviorLoopId;
        string? behaviorLoopSourceId;
        string? behaviorLoopSettingsJson;
        bool isInternal;

        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            loadedProfileId = reader.GetString(0);
            displayName = reader.GetString(1);
            description = reader.IsDBNull(2) ? null : reader.GetString(2);
            instructions = reader.IsDBNull(3) ? null : reader.GetString(3);
            chatProviderId = reader.IsDBNull(4) ? null : reader.GetString(4);
            chatModelId = reader.IsDBNull(5) ? null : reader.GetString(5);
            embeddingProviderId = reader.IsDBNull(6) ? null : reader.GetString(6);
            embeddingModelId = reader.IsDBNull(7) ? null : reader.GetString(7);
            createdAtUtc = DateTimeOffset.Parse(reader.GetString(8));
            updatedAtUtc = DateTimeOffset.Parse(reader.GetString(9));
            behaviorLoopId = reader.IsDBNull(10) ? null : reader.GetString(10);
            behaviorLoopSourceId = reader.IsDBNull(11) ? null : reader.GetString(11);
            behaviorLoopSettingsJson = reader.IsDBNull(12) ? null : reader.GetString(12);
            isInternal = !reader.IsDBNull(13) && reader.GetInt64(13) != 0;
        }

        return new AgentProfileRecord(
            loadedProfileId,
            displayName,
            description,
            instructions,
            chatProviderId,
            chatModelId,
            embeddingProviderId,
            embeddingModelId,
            createdAtUtc,
            updatedAtUtc,
            ListProfileModelBindings(connection, loadedProfileId, chatProviderId, chatModelId, embeddingProviderId, embeddingModelId),
            ListProfileSelectableCapabilityAssignments(connection, loadedProfileId),
            behaviorLoopId,
            behaviorLoopSourceId,
            behaviorLoopSettingsJson,
            isInternal);
    }

    public AgentProfileModelBindingRecord? GetProfileModelBinding(string profileId, string capabilityKind)
    {
        using var connection = CreateConnection();
        connection.Open();
        return GetProfileModelBinding(connection, profileId, capabilityKind);
    }

    public IReadOnlyList<AgentProfileModelBindingRecord> ListProfileModelBindings(string profileId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return ListProfileModelBindings(connection, profileId);
    }

    public void SaveProfile(AgentProfileRecord profile)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AgentProfiles (ProfileId, DisplayName, Description, Instructions, ProviderId, ModelId, EmbeddingProviderId, EmbeddingModelId, CreatedAtUtc, UpdatedAtUtc, BehaviorLoopId, BehaviorLoopSourceId, BehaviorLoopSettingsJson, IsInternal)
            VALUES ($id, $name, $description, $instructions, $providerId, $modelId, $embeddingProviderId, $embeddingModelId, $created, $updated, $behaviorLoopId, $behaviorLoopSourceId, $behaviorLoopSettingsJson, $isInternal)
            ON CONFLICT(ProfileId) DO UPDATE SET
                DisplayName = excluded.DisplayName,
                Description = excluded.Description,
                Instructions = excluded.Instructions,
                ProviderId = excluded.ProviderId,
                ModelId = excluded.ModelId,
                EmbeddingProviderId = excluded.EmbeddingProviderId,
                EmbeddingModelId = excluded.EmbeddingModelId,
                BehaviorLoopId = excluded.BehaviorLoopId,
                BehaviorLoopSourceId = excluded.BehaviorLoopSourceId,
                BehaviorLoopSettingsJson = excluded.BehaviorLoopSettingsJson,
                IsInternal = excluded.IsInternal,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$id", profile.ProfileId);
        command.Parameters.AddWithValue("$name", profile.DisplayName);
        command.Parameters.AddWithValue("$description", (object?)profile.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$instructions", (object?)profile.Instructions ?? DBNull.Value);
        command.Parameters.AddWithValue("$providerId", (object?)profile.ChatProviderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$modelId", (object?)profile.ChatModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$embeddingProviderId", (object?)profile.EmbeddingProviderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$embeddingModelId", (object?)profile.EmbeddingModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", profile.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", profile.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$behaviorLoopId", (object?)profile.BehaviorLoopId ?? DBNull.Value);
        command.Parameters.AddWithValue("$behaviorLoopSourceId", (object?)profile.BehaviorLoopSourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$behaviorLoopSettingsJson", (object?)profile.BehaviorLoopSettingsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$isInternal", profile.IsInternal ? 1 : 0);
        command.ExecuteNonQuery();

        var selectableAssignments = NormalizeSelectableAssignments(profile);
        ReplaceProfileModelBindings(connection, transaction, profile.ProfileId, NormalizeModelBindings(profile));
        ReplaceProfileSelectableCapabilityAssignments(connection, transaction, profile.ProfileId, selectableAssignments);
        transaction.Commit();
    }

    public void DeleteProfile(string profileId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var deleteProfileCommand = connection.CreateCommand())
        {
            deleteProfileCommand.Transaction = transaction;
            deleteProfileCommand.CommandText = "DELETE FROM AgentProfiles WHERE ProfileId = $id;";
            deleteProfileCommand.Parameters.AddWithValue("$id", profileId);
            deleteProfileCommand.ExecuteNonQuery();
        }

        using (var deleteModelBindingsCommand = connection.CreateCommand())
        {
            deleteModelBindingsCommand.Transaction = transaction;
            deleteModelBindingsCommand.CommandText = "DELETE FROM AgentProfileModelBindings WHERE ProfileId = $id;";
            deleteModelBindingsCommand.Parameters.AddWithValue("$id", profileId);
            deleteModelBindingsCommand.ExecuteNonQuery();
        }

        using (var deleteSelectableAssignmentsCommand = connection.CreateCommand())
        {
            deleteSelectableAssignmentsCommand.Transaction = transaction;
            deleteSelectableAssignmentsCommand.CommandText = "DELETE FROM AgentProfileSelectableCapabilityAssignments WHERE ProfileId = $id;";
            deleteSelectableAssignmentsCommand.Parameters.AddWithValue("$id", profileId);
            deleteSelectableAssignmentsCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static IReadOnlyList<AgentProfileRecord> ListProfiles(SqliteConnection connection)
    {
        var selectableAssignments = ListAllProfileSelectableCapabilityAssignments(connection);
        var modelBindings = ListAllProfileModelBindings(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProfileId, DisplayName, Description, Instructions, ProviderId, ModelId, EmbeddingProviderId, EmbeddingModelId, CreatedAtUtc, UpdatedAtUtc, BehaviorLoopId, BehaviorLoopSourceId, BehaviorLoopSettingsJson, IsInternal FROM AgentProfiles WHERE IsInternal = 0 ORDER BY DisplayName;";

        using var reader = command.ExecuteReader();
        var items = new List<AgentProfileRecord>();
        while (reader.Read())
        {
            var profileId = reader.GetString(0);
            items.Add(new AgentProfileRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                DateTimeOffset.Parse(reader.GetString(8)),
                DateTimeOffset.Parse(reader.GetString(9)),
                modelBindings.TryGetValue(profileId, out var profileModelBindings)
                    ? profileModelBindings
                    : BuildModelBindingsFromProfileFields(
                        profileId,
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        DateTimeOffset.Parse(reader.GetString(9))),
                selectableAssignments.TryGetValue(profileId, out var profileSelectableAssignments) ? profileSelectableAssignments : [],
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                !reader.IsDBNull(13) && reader.GetInt64(13) != 0
            ));
        }

        return items;
    }

    private static Dictionary<string, IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>> ListAllProfileSelectableCapabilityAssignments(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProfileId, Kind, CapabilityId, SourceId FROM AgentProfileSelectableCapabilityAssignments ORDER BY ProfileId, Kind, CapabilityId, SourceId;";

        using var reader = command.ExecuteReader();
        var assignments = new Dictionary<string, List<AgentProfileSelectableCapabilityAssignmentRecord>>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var profileId = reader.GetString(0);
            if (!assignments.TryGetValue(profileId, out var profileAssignments))
            {
                profileAssignments = [];
                assignments[profileId] = profileAssignments;
            }

            profileAssignments.Add(new AgentProfileSelectableCapabilityAssignmentRecord(
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) || string.IsNullOrWhiteSpace(reader.GetString(3)) ? null : reader.GetString(3)));
        }

        return assignments.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>)kvp.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> ListProfileSelectableCapabilityAssignments(SqliteConnection connection, string profileId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Kind, CapabilityId, SourceId FROM AgentProfileSelectableCapabilityAssignments WHERE ProfileId = $profileId ORDER BY Kind, CapabilityId, SourceId;";
        command.Parameters.AddWithValue("$profileId", profileId);

        using var reader = command.ExecuteReader();
        var assignments = new List<AgentProfileSelectableCapabilityAssignmentRecord>();
        while (reader.Read())
        {
            assignments.Add(new AgentProfileSelectableCapabilityAssignmentRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2)) ? null : reader.GetString(2)));
        }

        return assignments;
    }

    private static void ReplaceProfileSelectableCapabilityAssignments(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string profileId,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? assignments)
    {
        using (var deleteCommand = connection.CreateCommand())
        {
            if (transaction is not null)
            {
                deleteCommand.Transaction = transaction;
            }

            deleteCommand.CommandText = "DELETE FROM AgentProfileSelectableCapabilityAssignments WHERE ProfileId = $profileId;";
            deleteCommand.Parameters.AddWithValue("$profileId", profileId);
            deleteCommand.ExecuteNonQuery();
        }

        foreach (var assignment in assignments ?? [])
        {
            using var insertCommand = connection.CreateCommand();
            if (transaction is not null)
            {
                insertCommand.Transaction = transaction;
            }

            insertCommand.CommandText = "INSERT INTO AgentProfileSelectableCapabilityAssignments (ProfileId, Kind, CapabilityId, SourceId) VALUES ($profileId, $kind, $capabilityId, $sourceId);";
            insertCommand.Parameters.AddWithValue("$profileId", profileId);
            insertCommand.Parameters.AddWithValue("$kind", assignment.Kind);
            insertCommand.Parameters.AddWithValue("$capabilityId", assignment.CapabilityId);
            insertCommand.Parameters.AddWithValue("$sourceId", assignment.SourceId ?? string.Empty);
            insertCommand.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> NormalizeSelectableAssignments(AgentProfileRecord profile)
    {
        var assignments = (profile.SelectableCapabilityAssignments ?? [])
            .Where(assignment => !string.IsNullOrWhiteSpace(assignment.Kind)
                                 && !string.IsNullOrWhiteSpace(assignment.CapabilityId))
            .Select(assignment => assignment with
            {
                Kind = assignment.Kind.Trim(),
                CapabilityId = assignment.CapabilityId.Trim(),
                SourceId = string.IsNullOrWhiteSpace(assignment.SourceId) ? null : assignment.SourceId.Trim(),
            })
            .Distinct()
            .ToArray();

        return assignments;
    }

    private static Dictionary<string, IReadOnlyList<AgentProfileModelBindingRecord>> ListAllProfileModelBindings(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProfileId, CapabilityKind, ProviderId, ModelId, SettingsJson, UpdatedAtUtc FROM AgentProfileModelBindings ORDER BY ProfileId, CapabilityKind;";

        using var reader = command.ExecuteReader();
        var bindings = new Dictionary<string, List<AgentProfileModelBindingRecord>>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var profileId = reader.GetString(0);
            if (!bindings.TryGetValue(profileId, out var profileBindings))
            {
                profileBindings = [];
                bindings[profileId] = profileBindings;
            }

            profileBindings.Add(ReadModelBinding(reader));
        }

        return bindings.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<AgentProfileModelBindingRecord>)kvp.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static AgentProfileModelBindingRecord? GetProfileModelBinding(SqliteConnection connection, string profileId, string capabilityKind)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(capabilityKind))
        {
            return null;
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT ProfileId, CapabilityKind, ProviderId, ModelId, SettingsJson, UpdatedAtUtc FROM AgentProfileModelBindings WHERE ProfileId = $profileId AND CapabilityKind = $capabilityKind;";
            command.Parameters.AddWithValue("$profileId", profileId);
            command.Parameters.AddWithValue("$capabilityKind", capabilityKind);

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return ReadModelBinding(reader);
            }
        }

        using var profileCommand = connection.CreateCommand();
        profileCommand.CommandText = "SELECT ProviderId, ModelId, EmbeddingProviderId, EmbeddingModelId, UpdatedAtUtc FROM AgentProfiles WHERE ProfileId = $profileId;";
        profileCommand.Parameters.AddWithValue("$profileId", profileId);
        using var profileReader = profileCommand.ExecuteReader();
        if (!profileReader.Read())
        {
            return null;
        }

        var updatedAtUtc = DateTimeOffset.Parse(profileReader.GetString(4));
        return string.Equals(capabilityKind, AgentModelCapabilityKinds.Chat, StringComparison.OrdinalIgnoreCase)
            ? BuildModelBindingFromProfileFields(
                profileId,
                AgentModelCapabilityKinds.Chat,
                profileReader.IsDBNull(0) ? null : profileReader.GetString(0),
                profileReader.IsDBNull(1) ? null : profileReader.GetString(1),
                updatedAtUtc)
            : string.Equals(capabilityKind, AgentModelCapabilityKinds.Embedding, StringComparison.OrdinalIgnoreCase)
                ? BuildModelBindingFromProfileFields(
                    profileId,
                    AgentModelCapabilityKinds.Embedding,
                    profileReader.IsDBNull(2) ? null : profileReader.GetString(2),
                    profileReader.IsDBNull(3) ? null : profileReader.GetString(3),
                    updatedAtUtc)
                : null;
    }

    private static IReadOnlyList<AgentProfileModelBindingRecord> ListProfileModelBindings(SqliteConnection connection, string profileId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProfileId, CapabilityKind, ProviderId, ModelId, SettingsJson, UpdatedAtUtc FROM AgentProfileModelBindings WHERE ProfileId = $profileId ORDER BY CapabilityKind;";
        command.Parameters.AddWithValue("$profileId", profileId);

        using var reader = command.ExecuteReader();
        var bindings = new List<AgentProfileModelBindingRecord>();
        while (reader.Read())
        {
            bindings.Add(ReadModelBinding(reader));
        }

        if (bindings.Count > 0)
        {
            return bindings;
        }

        using var profileCommand = connection.CreateCommand();
        profileCommand.CommandText = "SELECT ProviderId, ModelId, EmbeddingProviderId, EmbeddingModelId, UpdatedAtUtc FROM AgentProfiles WHERE ProfileId = $profileId;";
        profileCommand.Parameters.AddWithValue("$profileId", profileId);
        using var profileReader = profileCommand.ExecuteReader();
        return profileReader.Read()
            ? BuildModelBindingsFromProfileFields(
                profileId,
                profileReader.IsDBNull(0) ? null : profileReader.GetString(0),
                profileReader.IsDBNull(1) ? null : profileReader.GetString(1),
                profileReader.IsDBNull(2) ? null : profileReader.GetString(2),
                profileReader.IsDBNull(3) ? null : profileReader.GetString(3),
                DateTimeOffset.Parse(profileReader.GetString(4)))
            : [];
    }

    private static IReadOnlyList<AgentProfileModelBindingRecord> ListProfileModelBindings(
        SqliteConnection connection,
        string profileId,
        string? chatProviderId,
        string? chatModelId,
        string? embeddingProviderId,
        string? embeddingModelId)
    {
        var bindings = ListProfileModelBindings(connection, profileId);
        return bindings.Count > 0
            ? bindings
            : BuildModelBindingsFromProfileFields(profileId, chatProviderId, chatModelId, embeddingProviderId, embeddingModelId, DateTimeOffset.UtcNow);
    }

    private static void ReplaceProfileModelBindings(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string profileId,
        IReadOnlyList<AgentProfileModelBindingRecord>? bindings)
    {
        using (var deleteCommand = connection.CreateCommand())
        {
            if (transaction is not null)
            {
                deleteCommand.Transaction = transaction;
            }

            deleteCommand.CommandText = "DELETE FROM AgentProfileModelBindings WHERE ProfileId = $profileId;";
            deleteCommand.Parameters.AddWithValue("$profileId", profileId);
            deleteCommand.ExecuteNonQuery();
        }

        foreach (var binding in bindings ?? [])
        {
            using var insertCommand = connection.CreateCommand();
            if (transaction is not null)
            {
                insertCommand.Transaction = transaction;
            }

            insertCommand.CommandText = """
                INSERT INTO AgentProfileModelBindings (ProfileId, CapabilityKind, ProviderId, ModelId, SettingsJson, UpdatedAtUtc)
                VALUES ($profileId, $capabilityKind, $providerId, $modelId, $settingsJson, $updatedAtUtc);
                """;
            insertCommand.Parameters.AddWithValue("$profileId", binding.ProfileId);
            insertCommand.Parameters.AddWithValue("$capabilityKind", binding.CapabilityKind);
            insertCommand.Parameters.AddWithValue("$providerId", (object?)binding.ProviderId ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$modelId", (object?)binding.ModelId ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$settingsJson", (object?)binding.SettingsJson ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$updatedAtUtc", binding.UpdatedAtUtc.ToString("O"));
            insertCommand.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<AgentProfileModelBindingRecord> NormalizeModelBindings(AgentProfileRecord profile)
    {
        var now = DateTimeOffset.UtcNow;
        var bindings = new Dictionary<string, AgentProfileModelBindingRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in profile.ModelBindings ?? [])
        {
            if (!string.IsNullOrWhiteSpace(binding.CapabilityKind)
                && (!string.IsNullOrWhiteSpace(binding.ProviderId) || !string.IsNullOrWhiteSpace(binding.ModelId)))
            {
                bindings[binding.CapabilityKind.Trim()] = binding with
                {
                    ProfileId = profile.ProfileId,
                    CapabilityKind = binding.CapabilityKind.Trim(),
                    ProviderId = string.IsNullOrWhiteSpace(binding.ProviderId) ? null : binding.ProviderId.Trim(),
                    ModelId = string.IsNullOrWhiteSpace(binding.ModelId) ? null : binding.ModelId.Trim(),
                    UpdatedAtUtc = binding.UpdatedAtUtc == default ? now : binding.UpdatedAtUtc,
                };
            }
        }

        AddProfileFieldModelBinding(bindings, profile.ProfileId, AgentModelCapabilityKinds.Chat, profile.ChatProviderId, profile.ChatModelId, now);
        AddProfileFieldModelBinding(bindings, profile.ProfileId, AgentModelCapabilityKinds.Embedding, profile.EmbeddingProviderId, profile.EmbeddingModelId, now);

        return bindings.Values
            .OrderBy(binding => binding.CapabilityKind, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<AgentProfileModelBindingRecord> BuildModelBindingsFromProfileFields(
        string profileId,
        string? chatProviderId,
        string? chatModelId,
        string? embeddingProviderId,
        string? embeddingModelId,
        DateTimeOffset updatedAtUtc)
        => new[]
            {
                BuildModelBindingFromProfileFields(profileId, AgentModelCapabilityKinds.Chat, chatProviderId, chatModelId, updatedAtUtc),
                BuildModelBindingFromProfileFields(profileId, AgentModelCapabilityKinds.Embedding, embeddingProviderId, embeddingModelId, updatedAtUtc),
            }
            .Where(binding => binding is not null)
            .Select(binding => binding!)
            .ToArray();

    private static AgentProfileModelBindingRecord? BuildModelBindingFromProfileFields(
        string profileId,
        string capabilityKind,
        string? providerId,
        string? modelId,
        DateTimeOffset updatedAtUtc)
        => string.IsNullOrWhiteSpace(providerId) && string.IsNullOrWhiteSpace(modelId)
            ? null
            : new(
                profileId,
                capabilityKind,
                string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim(),
                string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim(),
                SettingsJson: null,
                updatedAtUtc);

    private static void AddProfileFieldModelBinding(
        Dictionary<string, AgentProfileModelBindingRecord> bindings,
        string profileId,
        string capabilityKind,
        string? providerId,
        string? modelId,
        DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(providerId) && string.IsNullOrWhiteSpace(modelId))
        {
            bindings.Remove(capabilityKind);
            return;
        }

        bindings.TryGetValue(capabilityKind, out var existingBinding);
        bindings[capabilityKind] = (existingBinding ?? new AgentProfileModelBindingRecord(
            profileId,
            capabilityKind,
            providerId,
            modelId,
            SettingsJson: null,
            updatedAtUtc)) with
        {
            ProfileId = profileId,
            CapabilityKind = capabilityKind,
            ProviderId = string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim(),
            ModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim(),
            UpdatedAtUtc = updatedAtUtc,
        };
    }

    private static AgentProfileModelBindingRecord ReadModelBinding(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5)));
}
