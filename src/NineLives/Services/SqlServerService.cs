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

        // No UserID or Password here - SQL auth credentials go on the SqlConnection as a
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

        if (server.AuthMode != AuthMode.SqlAuth)
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

    public async Task ExecuteRestoreWithProgressAsync(
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
                throw new OperationCanceledException("The restore was cancelled.", ct);
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

    /// <summary>Checks if a credential with the given name exists on the server and has identity SHARED ACCESS SIGNATURE (for blob URL restores).</summary>
    public async Task<(bool Exists, bool IsSharedAccessSignature)> CredentialExistsAsync(
        ServerConnection server, string credentialName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credentialName))
            return (false, false);

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
            return (false, false);

        var identity = reader["credential_identity"]?.ToString() ?? "";
        var isSas = identity.Equals("SHARED ACCESS SIGNATURE", StringComparison.OrdinalIgnoreCase);
        return (true, isSas);
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

        // ALTER also resets IDENTITY, so a credential that exists under some other identity is
        // converted rather than left in place to fail the restore later.
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
