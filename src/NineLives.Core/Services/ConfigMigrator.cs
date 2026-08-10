using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Moves stored secrets off name-derived keys and onto stable per-entry ids (#8).
///
/// Runs once at startup. Entries saved before ids existed have no <c>Id</c>, and their SAS token
/// or password sits under <c>NineLives:Blob:{Name}</c> / <c>NineLives:SQL:{Name}</c>. This assigns
/// an id, copies the secret to the new key, saves the config, and only then removes the old key.
///
/// The ordering is the whole point, because this runs against the only copy of someone's
/// credentials and can be interrupted by a crash, a power cut or a refused config write:
///
///   1. copy secrets to the new keys, leaving the old ones in place
///   2. save the config carrying the new ids
///   3. delete the old keys
///
/// Interrupted after 1, the config still has no ids, so the next run reads the same old keys and
/// starts over - the stray new-key entries are orphans, not losses. Interrupted after 2, the
/// secrets are already where the ids point. There is no window in which a secret exists only under
/// a key nothing references.
/// </summary>
public class ConfigMigrator(ICredentialStore store)
{
    public record Result(int ContainersMigrated, int ServersMigrated, string? Error)
    {
        public bool DidWork => ContainersMigrated > 0 || ServersMigrated > 0;
    }

    /// <summary>
    /// Assigns ids to any entry lacking one and relocates its secret. Returns what happened rather
    /// than throwing: a migration that cannot complete must not stop the app from starting, since
    /// everything still works off the old keys until it succeeds.
    /// </summary>
    public Result Migrate(AppConfig config)
    {
        // A config that failed to load holds empty defaults, so there is nothing to migrate and
        // saving it would be refused anyway.
        if (config.LoadError != null)
            return new Result(0, 0, null);

        var containers = config.BlobContainers.Where(c => string.IsNullOrEmpty(c.Id)).ToList();
        var servers = config.Servers.Where(s => string.IsNullOrEmpty(s.Id)).ToList();

        if (containers.Count == 0 && servers.Count == 0)
            return new Result(0, 0, null);

        // Step 1: copy each secret to its new key while the old key still exists.
        var oldKeys = new List<string>();
        var newKeys = new List<string>();
        try
        {
            foreach (var container in containers)
            {
                var legacyKey = container.LegacyCredentialKey;
                var (username, secret) = store.ReadSecret(legacyKey);
                container.Id = BlobContainerConfig.NewId();

                if (secret == null) continue;   // nothing stored; the id alone is the migration
                store.SaveSecret(container.CredentialKey, username ?? "azure", secret);
                oldKeys.Add(legacyKey);
                newKeys.Add(container.CredentialKey);
            }

            foreach (var server in servers)
            {
                var legacyKey = server.LegacyCredentialKey;
                var (username, secret) = store.ReadSecret(legacyKey);
                server.Id = ServerConnection.NewId();

                if (secret == null) continue;
                store.SaveSecret(server.CredentialKey, username ?? server.Username ?? "sa", secret);
                oldKeys.Add(legacyKey);
                newKeys.Add(server.CredentialKey);
            }
        }
        catch (Exception ex)
        {
            return Abandon($"Could not copy stored secrets: {ex.Message}");
        }

        // Step 2: persist the ids. Until this lands the migration has not really happened.
        try
        {
            store.SaveConfig(config);
        }
        catch (Exception ex)
        {
            return Abandon($"Could not save the updated configuration: {ex.Message}");
        }

        // Step 3: the old keys are now unreferenced. Failing to remove one leaves a harmless
        // orphan in Credential Manager, which is not worth failing a completed migration over.
        foreach (var key in oldKeys)
        {
            try { store.DeleteSecret(key); } catch { /* orphan, not a failure */ }
        }

        return new Result(containers.Count, servers.Count, null);

        // Give up cleanly: put the in-memory objects back to matching what is on disk, and remove
        // the new-key copies written before the failure. Those ids are freshly generated and were
        // never persisted, so nothing else can be referring to them. The old keys are untouched
        // throughout and remain the ones the saved config points at.
        Result Abandon(string error)
        {
            foreach (var key in newKeys)
            {
                try { store.DeleteSecret(key); } catch { /* orphan, not a failure */ }
            }
            foreach (var container in containers) container.Id = null;
            foreach (var server in servers) server.Id = null;
            return new Result(0, 0, error);
        }
    }
}
