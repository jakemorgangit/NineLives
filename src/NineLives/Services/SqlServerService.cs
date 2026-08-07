using System.Data;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

public class SqlServerService : ISqlServerService
{
    private readonly ICredentialStore _credentialStore;

    public SqlServerService(ICredentialStore credentialStore)
    {
        _credentialStore = credentialStore;
    }

    public string BuildConnectionString(ServerConnection server)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server.ServerName,
            ConnectTimeout = server.ConnectionTimeoutSeconds,
            TrustServerCertificate = server.TrustServerCertificate,
            Encrypt = server.Encrypt switch
            {
                EncryptMode.Yes => SqlConnectionEncryptOption.Mandatory,
                EncryptMode.Strict => SqlConnectionEncryptOption.Strict,
                _ => SqlConnectionEncryptOption.Optional
            },
            ApplicationName = "Nine Lives",
            MultipleActiveResultSets = false
        };

        builder.IntegratedSecurity = server.AuthMode == AuthMode.WindowsAuth;

        // Entra is the driver's own token flow - Microsoft.Data.SqlClient acquires and refreshes
        // the token through MSAL, and the app never sees or stores a secret for it (#30).
        if (server.AuthMode.IsEntra())
        {
            // Before the connection string names a method, make sure the provider servicing it has
            // a window to parent its prompt to - otherwise the sign-in fails with
            // "a window handle must be configured" rather than showing anything.
            EntraAuthentication.Register(EntraAuthentication.ActiveWindowHandle);

            builder.Authentication = server.AuthMode switch
            {
                AuthMode.EntraInteractive => SqlAuthenticationMethod.ActiveDirectoryInteractive,
                AuthMode.EntraIntegrated => SqlAuthenticationMethod.ActiveDirectoryIntegrated,
                _ => SqlAuthenticationMethod.ActiveDirectoryDefault
            };

            // A hint for the account picker, not a credential. Safe in the connection string for
            // the same reason the SQL auth username is not: there is no password to pair it with.
            if (server.AuthMode == AuthMode.EntraInteractive && !string.IsNullOrWhiteSpace(server.Username))
                builder.UserID = server.Username;
        }

        // No UserID or Password here for SQL auth - those credentials go on the SqlConnection as a
        // SqlCredential instead (#20). A connection string is a long-lived managed string that
        // cannot be zeroed and turns up in crash dumps and memory captures; SqlCredential holds
        // the password in a SecureString the driver disposes. It also means anything that logs or
        // displays a connection string cannot leak the password by accident.
        //
        // SqlCredential refuses to attach to a connection string that already carries either, so
        // leaving them out is required rather than merely tidy.
        return builder.ConnectionString;
    }

    /// <summary>
    /// Opens nothing; builds the connection with the password attached out-of-band.
    /// Every SQL operation in this class goes through here.
    /// </summary>
    public SqlConnection CreateConnection(ServerConnection server)
    {
        var conn = new SqlConnection(BuildConnectionString(server));

        // Windows auth and the Entra modes carry no password of ours - the driver handles both.
        if (!server.AuthMode.NeedsStoredPassword())
            return conn;

        // Unsaved wins, so Test Connection can try a password without persisting it first (#12).
        var password = server.UnsavedPassword ?? _credentialStore.GetSqlPassword(server);
        if (string.IsNullOrEmpty(server.Username) || password == null)
            return conn;

        var secure = new SecureString();
        foreach (var c in password) secure.AppendChar(c);
        secure.MakeReadOnly();

        conn.Credential = new SqlCredential(server.Username, secure);
        return conn;
    }

    public async Task<bool> TestConnectionAsync(ServerConnection server, CancellationToken ct = default)
    {
        await using var conn = CreateConnection(server);
        await conn.OpenAsync(ct);
        return conn.State == ConnectionState.Open;
    }

    /// <summary>
    /// Would this server connect with certificate validation switched on?
    ///
    /// TrustServerCertificate defaults to true, which is the pragmatic default for a DBA tool -
    /// self-signed certificates are what a stock SQL Server install has, and a tool that refuses
    /// to connect out of the box gets uninstalled. But it means the app accepts any certificate
    /// without validation, and it sends the SQL password and the SAS token over that connection
    /// (#17).
    ///
    /// Rather than nag about it, this answers the question that actually matters: is the setting
    /// doing anything? A great many instances have a properly issued certificate and are trusting
    /// blindly for no reason at all. When that is the case the UI can say so and the user can turn
    /// it off with real information rather than a warning they will learn to ignore.
    ///
    /// Returns null when the answer is not knowable - the probe failed for some reason other than
    /// the certificate.
    /// </summary>
    public async Task<bool?> WouldConnectWithCertificateValidationAsync(
        ServerConnection server, CancellationToken ct = default)
    {
        if (!server.TrustServerCertificate)
            return true;   // already validating

        var validated = new ServerConnection
        {
            Id = server.Id,
            Name = server.Name,
            ServerName = server.ServerName,
            AuthMode = server.AuthMode,
            Username = server.Username,
            ConnectionTimeoutSeconds = server.ConnectionTimeoutSeconds,
            Encrypt = server.Encrypt,
            TrustServerCertificate = false,
            UnsavedPassword = server.UnsavedPassword
        };

        try
        {
            await using var conn = CreateConnection(validated);
            await conn.OpenAsync(ct);
            return true;
        }
        catch (SqlException ex) when (IsCertificateFailure(ex))
        {
            return false;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// SQL Server surfaces a rejected certificate as a generic SSL provider error, so this matches
    /// on the message. Getting it wrong only costs a "could not determine" instead of a definite
    /// answer, which is why the caller treats null as "say nothing".
    /// </summary>
    private static bool IsCertificateFailure(SqlException ex)
    {
        var message = ex.ToString();
        return message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("trust relationship", StringComparison.OrdinalIgnoreCase)
            || message.Contains("SSL", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> GetServerVersionAsync(ServerConnection server, CancellationToken ct = default)
    {
        await using var conn = CreateConnection(server);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT @@VERSION";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString() ?? "Unknown";
    }

    /// <summary>
    /// Reads what state a database is in, so a failed restore can say what it left behind (#14).
    /// Parameterised - the name comes from a text box.
    /// </summary>
    public async Task<DatabaseRecoveryState> GetDatabaseRecoveryStateAsync(
        ServerConnection server, string databaseName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
            return DatabaseRecoveryState.Missing;

        await using var conn = CreateConnection(server);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT state_desc, user_access_desc FROM sys.databases WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", databaseName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return DatabaseRecoveryState.Missing;

        return new DatabaseRecoveryState(
            true,
            reader["state_desc"]?.ToString(),
            reader["user_access_desc"]?.ToString());
    }

    /// <summary>
    /// Runs a single remediation statement chosen by the user from
    /// <see cref="DatabaseRecoveryState.SuggestedActions"/>. Deliberately takes the whole statement
    /// rather than building one - the user has already been shown exactly what will run.
    /// </summary>
    public async Task ExecuteRecoveryActionAsync(
        ServerConnection server, string sql, CancellationToken ct = default)
    {
        await using var conn = CreateConnection(server);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = sql;

        await CancellableAsync(
            () => cmd.ExecuteNonQueryAsync(ct), ct, "The recovery action");
    }

    public async Task<List<string>> GetDatabaseListAsync(ServerConnection server, CancellationToken ct = default)
    {
        await using var conn = CreateConnection(server);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys.databases ORDER BY name";
        var databases = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            databases.Add(reader.GetString(0));
        return databases;
    }

    /// <summary>
    /// What this instance recorded backing up, read from msdb (#149).
    ///
    /// The first half of restoring from a shared backup location: there is no container to list,
    /// but the instance that took the backups knows exactly what it took, when, to which files and
    /// at which LSNs.
    ///
    /// Ordered newest first, and capped, because msdb on a busy instance holds years of history and
    /// a restore is almost always from the recent end of it. A cap is honest about what it returns -
    /// see BackupHistoryLimit.
    ///
    /// It reports what msdb SAYS. Whether those files still exist is a different question, asked
    /// separately and on the target, because msdb keeps history for backups whose files were
    /// deleted, archived or pruned by a retention job long ago.
    /// </summary>
    public async Task<List<BackupHistoryEntry>> ReadBackupHistoryAsync(
        ServerConnection server, string? databaseName = null, CancellationToken ct = default)
    {
        await using var conn = CreateConnection(server);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();

        // backupset holds one row per backup; backupmediafamily one per FILE it was written to, so
        // a striped backup is several rows and family_sequence_number is the stripe order. Grouping
        // in the app rather than with STRING_AGG keeps this readable on every supported version.
        cmd.CommandText = $@"
            SELECT TOP ({BackupHistoryLimit})
                   bs.backup_set_id,
                   bs.database_name,
                   bs.type,
                   bs.backup_start_date,
                   bs.backup_finish_date,
                   bs.is_copy_only,
                   bs.first_lsn,
                   bs.last_lsn,
                   bs.checkpoint_lsn,
                   bs.database_backup_lsn,
                   bs.backup_size,
                   bs.server_name,
                   bmf.physical_device_name,
                   bmf.family_sequence_number
            FROM msdb.dbo.backupset AS bs
            JOIN msdb.dbo.backupmediafamily AS bmf
              ON bmf.media_set_id = bs.media_set_id
            WHERE (@database IS NULL OR bs.database_name = @database)
              AND bmf.device_type IN (2, 102)   -- disk, including logical disk devices
            ORDER BY bs.backup_start_date DESC, bs.backup_set_id DESC, bmf.family_sequence_number";

        cmd.Parameters.AddWithValue("@database", (object?)databaseName ?? DBNull.Value);

        // One row per FILE, gathered back into one entry per backup set.
        var files = new Dictionary<int, List<(int Sequence, string Path)>>();
        var sets = new Dictionary<int, BackupHistoryEntry>();
        var order = new List<int>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt32(0);

            if (!sets.ContainsKey(id))
            {
                order.Add(id);
                sets[id] = new BackupHistoryEntry
                {
                    DatabaseName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    Type = TypeFromMsdb(reader.IsDBNull(2) ? null : reader.GetString(2)),
                    StartedAt = reader.GetDateTime(3),
                    FinishedAt = reader.IsDBNull(4) ? reader.GetDateTime(3) : reader.GetDateTime(4),
                    IsCopyOnly = !reader.IsDBNull(5) && reader.GetBoolean(5),
                    FirstLsn = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    LastLsn = reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                    CheckpointLsn = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    DatabaseBackupLsn = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                    BackupSizeBytes = reader.IsDBNull(10) ? null : (long)reader.GetDecimal(10),
                    ServerName = reader.IsDBNull(11) ? null : reader.GetString(11)
                };
                files[id] = [];
            }

            // family_sequence_number is tinyint, not int - GetInt32 throws an InvalidCastException
            // on it. Found by running this against a real instance rather than by reading the docs.
            if (!reader.IsDBNull(12))
                files[id].Add((
                    reader.IsDBNull(13) ? 1 : Convert.ToInt32(reader.GetValue(13)),
                    reader.GetString(12)));
        }

        return order
            .Select(id => CloneWithFiles(sets[id],
                files[id].OrderBy(f => f.Sequence).Select(f => f.Path).ToList()))
            .ToList();
    }

    /// <summary>
    /// How far back to read. msdb on a busy instance holds years of history, and a restore is
    /// almost always from the recent end of it - but the number is stated here rather than left
    /// implicit, because silently returning "the newest 500" while looking like "everything" is
    /// how somebody concludes a backup is missing when it is simply older than the cap.
    /// </summary>
    public const int BackupHistoryLimit = 500;

    private static BackupHistoryEntry CloneWithFiles(BackupHistoryEntry entry, List<string> files) => new()
    {
        DatabaseName = entry.DatabaseName,
        Type = entry.Type,
        StartedAt = entry.StartedAt,
        FinishedAt = entry.FinishedAt,
        IsCopyOnly = entry.IsCopyOnly,
        FirstLsn = entry.FirstLsn,
        LastLsn = entry.LastLsn,
        CheckpointLsn = entry.CheckpointLsn,
        DatabaseBackupLsn = entry.DatabaseBackupLsn,
        BackupSizeBytes = entry.BackupSizeBytes,
        ServerName = entry.ServerName,
        Files = files
    };

    /// <summary>
    /// msdb's one-letter backup type. D is a full, I a differential, L a log; the rest - file and
    /// filegroup backups, partial backups - are not chain members this app restores, and are
    /// reported as Unknown rather than guessed at.
    /// </summary>
    internal static BackupType TypeFromMsdb(string? type) => type switch
    {
        "D" => BackupType.Full,
        "I" => BackupType.Differential,
        "L" => BackupType.TransactionLog,
        _ => BackupType.Unknown
    };

    // These two read backup metadata off blob URLs, and neither takes a credential name any more.
    //
    // They used to, and passing one could never work. SQL Server rejects WITH CREDENTIAL against a
    // SAS-backed credential outright:
    //
    //     Msg 3225: Use of WITH CREDENTIAL syntax is not valid for credentials containing a
    //     Shared Access Signature.
    //
    // A SAS credential is the only kind this app ever creates - EnsureCredentialExistsAsync
    // hardcodes WITH IDENTITY = 'SHARED ACCESS SIGNATURE' - so the branch could not succeed in any
    // configuration the app produces. Both production callers already passed null, which is why
    // the shipping app worked, but the parameter was positional and required: a new caller had to
    // pass something, and the obvious something failed with an error about syntax rather than
    // about the real cause. Removing it makes the wrong call unrepresentable rather than merely
    // unused (#60).
    //
    // The server-side credential still does the authenticating. It is matched by URL prefix, which
    // is exactly how the generated restore script has always worked.
    //
    // If a non-SAS credential is ever supported (managed identity, #29), bring the option back
    // keyed off the identity type that CredentialExistsAsync already reports - not off a flag.

    /// <summary>RESTORE FILELISTONLY across the files of one backup set, striped or not.</summary>
    public async Task<List<FileMoveOption>> RestoreFileListOnlyAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, CancellationToken ct = default)
    {
        if (blobUrls.Count == 0) return [];

        await using var conn = CreateConnection(server);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;

        var urlClauses = string.Join(", ", blobUrls.Select(u => $"URL = N'{TSql.EscapeLiteral(u)}'"));
        cmd.CommandText = $"RESTORE FILELISTONLY FROM {urlClauses}";

        return await CancellableAsync(async () =>
        {
            var files = new List<FileMoveOption>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                files.Add(new FileMoveOption
                {
                    LogicalName = reader.GetString(reader.GetOrdinal("LogicalName")),
                    PhysicalName = reader.GetString(reader.GetOrdinal("PhysicalName")),
                    Type = reader.GetString(reader.GetOrdinal("Type")),
                    NewPhysicalName = reader.GetString(reader.GetOrdinal("PhysicalName"))
                });
            }
            return files;
        }, ct, "Reading the file list");
    }

    /// <summary>RESTORE HEADERONLY across the files of one backup set, striped or not.</summary>
    /// <summary>
    /// Asks the TARGET instance whether it can read a backup file, and says why not (#149).
    ///
    /// The reason this is a server round trip rather than File.Exists: the app's own process can
    /// see a share the SQL Server service account cannot, and the RESTORE runs as that account on
    /// that host. Checking locally would say yes and the restore would then fail with
    /// "Operating system error 5(Access is denied)" - so the question is asked where it will be
    /// answered.
    ///
    /// RESTORE HEADERONLY is the cheapest statement that proves all three things at once: the path
    /// resolves, the account may read it, and what is there is a backup. It reads the header only,
    /// not the backup.
    /// </summary>
    public async Task<BackupFileCheck> CheckBackupFileAsync(
        ServerConnection server, string path, CancellationToken ct = default)
    {
        try
        {
            await using var conn = CreateConnection(server);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();

            // Short. A file that is not reachable should fail quickly - this runs once per file and
            // the whole point is to find that out BEFORE a restore starts, not to wait as long as
            // one would.
            cmd.CommandTimeout = 30;
            cmd.CommandText = $"RESTORE HEADERONLY FROM DISK = N'{TSql.EscapeLiteral(path)}'";

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct)
                ? BackupFileCheck.Ok(path)
                : BackupFileCheck.From(path, "The file was read but contained no backup header.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Its own words, classified but never replaced: the numbered error is what somebody
            // searches for when the explanation does not match their situation.
            return BackupFileCheck.From(path, ex.Message);
        }
    }

    /// <summary>
    /// Checks every file a chain needs, stopping at the first that cannot be read.
    ///
    /// Stops early on purpose. One unreadable file means the restore cannot run, and the usual
    /// cause - the service account having no access to the share - fails every file identically,
    /// so carrying on would produce a page of the same message.
    /// </summary>
    public async Task<List<BackupFileCheck>> CheckBackupFilesAsync(
        ServerConnection server, IEnumerable<string> paths, CancellationToken ct = default)
    {
        var results = new List<BackupFileCheck>();

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();

            var check = await CheckBackupFileAsync(server, path, ct);
            results.Add(check);

            if (!check.CanBeRestored) break;
        }

        return results;
    }

    public async Task<BackupFileInfo?> RestoreHeaderOnlyMultiAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, CancellationToken ct = default)
    {
        if (blobUrls.Count == 0) return null;

        await using var conn = CreateConnection(server);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;

        var urlClauses = string.Join(", ", blobUrls.Select(u => $"URL = N'{TSql.EscapeLiteral(u)}'"));
        cmd.CommandText = $"RESTORE HEADERONLY FROM {urlClauses}";

        return await CancellableAsync<BackupFileInfo?>(async () =>
        {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        int? backupTypeCode = GetIntFromReader(reader, "BackupType");
        return new BackupFileInfo
        {
            DatabaseName = GetStringFromReader(reader, "DatabaseName"),
            BackupStartDate = GetDateTimeFromReader(reader, "BackupStartDate"),
            BackupFinishDate = GetDateTimeFromReader(reader, "BackupFinishDate"),
            BackupTypeCode = backupTypeCode,
            Type = (backupTypeCode ?? 0) switch
            {
                1 => BackupType.Full,
                5 => BackupType.Differential,
                2 => BackupType.TransactionLog,
                _ => BackupType.Unknown
            },
            FirstLsn = GetDecimalFromReader(reader, "FirstLSN"),
            LastLsn = GetDecimalFromReader(reader, "LastLSN"),
            DatabaseBackupLsn = GetDecimalFromReader(reader, "DatabaseBackupLSN"),
            CheckpointLsn = GetDecimalFromReader(reader, "CheckpointLSN")
        };
        }, ct, "Reading the backup header");
    }

    /// <summary>
    /// RESTORE VERIFYONLY across the files of one backup set, striped or not.
    ///
    /// This is the check a DBA runs before committing to a long restore: it reads the backup and
    /// reports whether it is complete and readable, in seconds rather than after an hour of
    /// restoring. It does NOT check the data inside - that needs a restore and DBCC CHECKDB.
    ///
    /// A failure is returned rather than thrown, so one unreadable member of a chain does not
    /// abort verification of the rest. Cancellation still propagates.
    /// </summary>
    /// <param name="withChecksum">
    /// Adds WITH CHECKSUM. Left off, SQL Server applies its own default; a backup taken without
    /// checksums FAILS this rather than skipping the check, so it is the caller's choice.
    /// </param>
    /// <param name="fileMoves">
    /// The same MOVE clauses the restore will use. VERIFYONLY checks whether a restore could
    /// proceed, which includes looking for the file paths it would write to - so without these it
    /// checks the paths recorded INSIDE the backup, which belong to the source server. Confirmed
    /// against SQL Server 2025: passing MOVE makes it check the move targets instead.
    /// </param>
    public async Task<VerifyOnlyResult> RestoreVerifyOnlyAsync(
        ServerConnection server,
        IReadOnlyList<string> blobUrls,
        bool withChecksum = false,
        IReadOnlyList<FileMoveOption>? fileMoves = null,
        CancellationToken ct = default)
    {
        if (blobUrls.Count == 0)
            return new VerifyOnlyResult(false, "No files to verify.");

        var messages = new List<string>();

        await using var conn = CreateConnection(server);

        // VERIFYONLY says what it found through info messages - "The backup set on file 1 is
        // valid." - and reports a bad backup by throwing. Both halves are wanted.
        conn.InfoMessage += (_, e) => messages.Add(e.Message);

        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;

        cmd.CommandText = BuildVerifyOnlyStatement(blobUrls, withChecksum, fileMoves);

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException) when (ct.IsCancellationRequested)
        {
            // Same trap as the restore path: a cancelled command surfaces as SqlException, and
            // reporting that as a verification failure would tell the user their backup is bad.
            throw new OperationCanceledException("Verification was cancelled.", ct);
        }
        catch (SqlException ex)
        {
            return new VerifyOnlyResult(false, ex.Message);
        }

        var report = messages.Count > 0
            ? string.Join(" ", messages.Select(m => m.Trim()))
            : "The backup set is valid.";

        return new VerifyOnlyResult(true, report, MentionsMissingDirectory(report));
    }

    /// <summary>
    /// Runs something against SQL Server and translates a cancellation into the exception the
    /// caller expects.
    ///
    /// SqlClient reports a command cancelled mid-flight as a SqlException - "A severe error
    /// occurred on the current command" - not an OperationCanceledException. Every call site that
    /// takes a token needs the same trap, and writing it out at each one is how three of them came
    /// to be missing it. Only when the token was actually signalled, so a genuine severe error
    /// still propagates as the failure it is.
    /// </summary>
    private static async Task<T> CancellableAsync<T>(
        Func<Task<T>> work, CancellationToken ct, string what)
    {
        try
        {
            return await work();
        }
        catch (SqlException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException($"{what} was cancelled.", ct);
        }
    }

    private static async Task CancellableAsync(
        Func<Task> work, CancellationToken ct, string what)
        => await CancellableAsync<bool>(async () => { await work(); return true; }, ct, what);

    /// <summary>
    /// The exact T-SQL a verification runs. Every file of a striped set goes into one statement -
    /// a stripe on its own is not a readable backup, so verifying them one at a time would report
    /// failures that are not there.
    ///
    /// No WITH CREDENTIAL clause: SQL Server rejects that for SAS credentials with Msg 3225, and
    /// it matches the credential by URL anyway (#60).
    /// </summary>
    public static string BuildVerifyOnlyStatement(
        IReadOnlyList<string> blobUrls,
        bool withChecksum,
        IReadOnlyList<FileMoveOption>? fileMoves = null)
    {
        var urlClauses = string.Join(", ", blobUrls.Select(u => $"URL = N'{TSql.EscapeLiteral(u)}'"));

        var options = new List<string>();

        // Omitted rather than set to NO_CHECKSUM when off, so SQL Server's own default applies.
        if (withChecksum) options.Add("CHECKSUM");

        // The same MOVE clauses the restore will use. Both names are string literals, not
        // identifiers - LogicalName comes from RESTORE FILELISTONLY (sysname, so an apostrophe is
        // legal) and NewPhysicalName is a user-editable path.
        if (fileMoves != null)
        {
            options.AddRange(fileMoves
                .Where(m => !string.IsNullOrWhiteSpace(m.NewPhysicalName))
                .Select(m => $"MOVE N'{TSql.EscapeLiteral(m.LogicalName)}' " +
                             $"TO N'{TSql.EscapeLiteral(m.NewPhysicalName)}'"));
        }

        return options.Count == 0
            ? $"RESTORE VERIFYONLY FROM {urlClauses}"
            : $"RESTORE VERIFYONLY FROM {urlClauses} WITH {string.Join(", ", options)}";
    }

    /// <summary>
    /// Did VERIFYONLY complain that it could not find the directories a restore would write to?
    ///
    /// Matched on SQL Server's own wording. It reports this as an informational message alongside
    /// "the backup set is valid", so a chain can be perfectly readable and still be certain to
    /// fail on restore - which is worth saying out loud rather than leaving in four lines of grey
    /// text under a green tick (#129).
    /// </summary>
    private static bool MentionsMissingDirectory(string report)
        => report.Contains("Directory lookup for the file", StringComparison.OrdinalIgnoreCase)
        || report.Contains("may encounter storage space problems", StringComparison.OrdinalIgnoreCase);

    private static string? GetStringFromReader(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTime? GetDateTimeFromReader(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dt => dt,
            DateTimeOffset dto => dto.DateTime,
            _ when value is IConvertible c => c.ToDateTime(null),
            _ => null
        };
    }

    private static decimal? GetDecimalFromReader(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            decimal d => d,
            long l => l,
            double db => (decimal)db,
            _ when value is IConvertible c => c.ToDecimal(null),
            _ => null
        };
    }

    /// <summary>RESTORE HEADERONLY returns BackupType as tinyint/smallint; avoid invalid cast.</summary>
    private static int? GetIntFromReader(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            byte b => b,
            short s => s,
            int i => i,
            long l => (int)l,
            _ when value is IConvertible c => c.ToInt32(null),
            _ => null
        };
    }

    // DO NOT set conn.FireInfoMessageEventOnUserErrors = true on these connections.
    //
    // That flag routes every error of severity <= 16 to the InfoMessage event INSTEAD of
    // throwing SqlException - and severity 16 is where SQL Server reports essentially every
    // real restore failure: 3201 (cannot open backup device, i.e. a bad or expired SAS),
    // 3013 (RESTORE terminating abnormally), 4305 (log too recent), 3136 (differential base
    // mismatch), 3154 (backup set is for a different database).
    //
    // With the flag on, nothing threw, the GO-split statement loop below ran the rest of the
    // chain against a database that was never created, and the caller reported
    // "Restore completed successfully!" over a total failure - the worst possible outcome for
    // a restore tool. Progress output does NOT depend on the flag: STATS "X percent processed"
    // and PRINT are severity <= 10 and arrive through InfoMessage either way.
    //
    // Pinned by SqlExecutionFailureTests (live SQL, gated on NINELIVES_TEST_SQL).

    public async Task ExecuteNonQueryAsync(
        ServerConnection server, string sql,
        Action<string>? messageCallback = null, CancellationToken ct = default)
    {
        await using var conn = CreateConnection(server);
        if (messageCallback != null)
        {
            conn.InfoMessage += (_, e) => messageCallback(e.Message);
        }
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ExecuteWithProgressAsync(
        ServerConnection server, string sql,
        Action<string>? messageCallback = null, CancellationToken ct = default)
    {
        await using var conn = CreateConnection(server);
        if (messageCallback != null)
        {
            conn.InfoMessage += (_, e) => messageCallback(e.Message);
        }
        await conn.OpenAsync(ct);

        var statements = SplitGoStatements(sql);
        var executable = statements.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        for (int i = 0; i < executable.Count; i++)
        {
            var statement = executable[i];
            ct.ThrowIfCancellationRequested();

            messageCallback?.Invoke(
                $"Executing statement {i + 1} of {executable.Count}: {Summarize(statement)}...");

            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = statement;

            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException) when (ct.IsCancellationRequested)
            {
                // SqlClient reports a command cancelled mid-flight as a SqlException - "A severe
                // error occurred on the current command. The results, if any, should be discarded.
                // Operation cancelled by user." - and NOT as an OperationCanceledException. Left
                // alone, a caller who catches OperationCanceledException to show "cancelled"
                // misses it entirely and tells the user their own Stop was a severe error.
                //
                // Only when the token was actually signalled, so a genuine severe error still
                // propagates as the failure it is.
                messageCallback?.Invoke(
                    $"Cancelled during statement {i + 1} of {executable.Count}: {Summarize(statement)}");
                throw new OperationCanceledException("Cancelled.", ct);
            }
            catch (SqlException)
            {
                // Say WHICH step of the chain failed before it propagates. Without this the
                // user sees a bare SQL error and has to work out whether the full, a
                // differential, or a log restore was the one that died.
                messageCallback?.Invoke(
                    $"FAILED on statement {i + 1} of {executable.Count}: {Summarize(statement)}");
                throw;
            }
        }
    }

    /// <summary>
    /// A one-line preview of a statement for the console.
    ///
    /// Whitespace is collapsed FIRST. Taking the first 80 characters raw pulled in the newlines
    /// and blank lines of the script's comment banner, so a single "Executing statement 1 of 22"
    /// message arrived as several lines with gaps between them.
    /// </summary>
    private static string Summarize(string statement)
    {
        var flattened = WhitespaceRun.Replace(statement, " ").Trim();
        return flattened.Length <= 80 ? flattened : flattened[..80] + "...";
    }

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Which credential of that name is on the server, if any, and what it authenticates as.
    ///
    /// Reports the identity rather than "is it SAS" (#145). The two identities a restore from URL
    /// can use are SHARED ACCESS SIGNATURE and, on SQL Server 2022+ or Azure SQL MI, Managed
    /// Identity; collapsing them into one bool told the caller a working managed-identity
    /// credential was broken, and the caller then overwrote it.
    /// </summary>
    public async Task<BlobCredentialStatus> CredentialExistsAsync(
        ServerConnection server, string credentialName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credentialName))
            return BlobCredentialStatus.Missing;

        await using var conn = CreateConnection(server);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT credential_identity
            FROM sys.credentials
            WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", credentialName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return BlobCredentialStatus.Missing;

        var identity = (reader["credential_identity"]?.ToString() ?? "").Trim();
        return new BlobCredentialStatus(ClassifyIdentity(identity), identity);
    }

    /// <summary>
    /// The identity text as stored in sys.credentials, matched loosely. Case is not guaranteed -
    /// the docs write 'Managed Identity' and 'MANAGED IDENTITY' in different places, and whoever
    /// created the credential typed one of them.
    /// </summary>
    internal static BlobCredentialIdentity ClassifyIdentity(string identity)
    {
        if (identity.Equals("SHARED ACCESS SIGNATURE", StringComparison.OrdinalIgnoreCase))
            return BlobCredentialIdentity.SharedAccessSignature;
        if (identity.Equals("Managed Identity", StringComparison.OrdinalIgnoreCase))
            return BlobCredentialIdentity.ManagedIdentity;
        return BlobCredentialIdentity.Other;
    }

    // CREATE/DROP CREDENTIAL cannot take the name as a parameter - it is an identifier, not a
    // value - so it must be quoted. Both statements previously interpolated it raw between
    // brackets without doubling an embedded ']', which meant the brackets did not actually
    // delimit anything: a name ending in '] WITH IDENTITY=... SECRET=...; <statement>; CREATE
    // CREDENTIAL [decoy' produced a well-formed multi-statement batch that ran on a connection
    // holding CONTROL SERVER. The name is auto-populated from the container URL in config.json
    // (plain text, no integrity check) and is editable free text in the UI, so it is attacker-
    // reachable via a single local file write.
    //
    // It also broke benignly: an IPv6-literal endpoint such as https://[fe80::1]:10000/... is a
    // legal URL containing ']' and failed with an opaque syntax error.
    //
    // An existing credential is now ALTERed rather than dropped and recreated. A credential is
    // server-scoped shared state: dropping it, even for the moment between two statements,
    // breaks anything else relying on it at that instant - a backup job writing to the same
    // container, most obviously. ALTER updates the secret in place with no such window, and
    // leaves create_date intact, which is how the tests tell the two apart.
    public async Task<CredentialChange> EnsureCredentialExistsAsync(
        ServerConnection server, string credentialName, string storageAccountUrl, string sasToken,
        CancellationToken ct = default)
    {
        TSql.ValidateIdentifier(credentialName, nameof(credentialName));
        var quotedName = TSql.QuoteName(credentialName);

        await using var conn = CreateConnection(server);
        await conn.OpenAsync(ct);

        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM sys.credentials WHERE name = @name";
        checkCmd.Parameters.AddWithValue("@name", credentialName);
        var exists = (int)(await checkCmd.ExecuteScalarAsync(ct))! > 0;

        // ALTER resets IDENTITY as well as the secret, so a credential sitting under some other
        // identity is converted rather than left to fail the restore later.
        //
        // That conversion is destructive and this method cannot tell a mistake from a deliberate
        // setup, so it must only ever be reached because somebody asked for it. The execute path
        // no longer calls this to "fix" an identity it does not recognise - it stops and says what
        // it found, because the identity it would have replaced could be a managed identity the
        // instance genuinely restores with (#145).
        var cleanSas = sasToken.TrimStart('?');
        await using var writeCmd = conn.CreateCommand();
        writeCmd.CommandText = $@"
            {(exists ? "ALTER" : "CREATE")} CREDENTIAL {quotedName}
            WITH IDENTITY = 'SHARED ACCESS SIGNATURE',
            SECRET = '{TSql.EscapeLiteral(cleanSas)}'";
        await writeCmd.ExecuteNonQueryAsync(ct);

        return exists ? CredentialChange.Updated : CredentialChange.Created;
    }

    public async Task<(string DataPath, string LogPath)> GetDefaultPathsAsync(
        ServerConnection server, CancellationToken ct = default)
    {
        await using var conn = CreateConnection(server);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT
            SERVERPROPERTY('InstanceDefaultDataPath') AS DefaultDataPath,
            SERVERPROPERTY('InstanceDefaultLogPath') AS DefaultLogPath";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var dataPath = reader["DefaultDataPath"]?.ToString() ?? string.Empty;
            var logPath = reader["DefaultLogPath"]?.ToString() ?? string.Empty;
            return (dataPath.TrimEnd('\\'), logPath.TrimEnd('\\'));
        }
        return (string.Empty, string.Empty);
    }

    private static List<string> SplitGoStatements(string sql)
    {
        var statements = new List<string>();
        var lines = sql.Split('\n');
        var current = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (current.Length > 0)
                {
                    statements.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.AppendLine(line);
            }
        }

        if (current.Length > 0)
            statements.Add(current.ToString());

        return statements;
    }
}
