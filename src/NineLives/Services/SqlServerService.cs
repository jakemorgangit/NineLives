using System.Data;
using System.Security;
using Microsoft.Data.SqlClient;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

public class SqlServerService
{
    private readonly CredentialStore _credentialStore;

    public SqlServerService(CredentialStore credentialStore)
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

    public async Task<List<BackupFileInfo>> RestoreHeaderOnlyAsync(
        ServerConnection server, string blobUrl, string credentialName, CancellationToken ct = default)
    {
        await using var conn = CreateConnection(server);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = $"RESTORE HEADERONLY FROM URL = N'{TSql.EscapeLiteral(blobUrl)}' WITH CREDENTIAL = N'{TSql.EscapeLiteral(credentialName)}'";

        var results = new List<BackupFileInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int? backupTypeCode = GetIntFromReader(reader, "BackupType");
            results.Add(new BackupFileInfo
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
            });
        }
        return results;
    }

    public async Task<List<FileMoveOption>> RestoreFileListOnlyAsync(
        ServerConnection server, string blobUrl, string credentialName, CancellationToken ct = default)
    {
        return await RestoreFileListOnlyAsync(server, [blobUrl], credentialName, urlsContainSas: false, ct);
    }

    /// <summary>RESTORE FILELISTONLY for striped backup. When urlsContainSas is true, omit WITH CREDENTIAL (SQL Server does not allow WITH CREDENTIAL for SAS credentials).</summary>
    public async Task<List<FileMoveOption>> RestoreFileListOnlyAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, string? credentialName, bool urlsContainSas = false, CancellationToken ct = default)
    {
        if (blobUrls.Count == 0) return [];

        await using var conn = CreateConnection(server);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;

        var urlClauses = string.Join(", ", blobUrls.Select(u => $"URL = N'{TSql.EscapeLiteral(u)}'"));
        var withCredential = !urlsContainSas && !string.IsNullOrEmpty(credentialName)
            ? $" WITH CREDENTIAL = N'{TSql.EscapeLiteral(credentialName)}'"
            : "";
        cmd.CommandText = $"RESTORE FILELISTONLY FROM {urlClauses}{withCredential}";

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
    }

    /// <summary>RESTORE HEADERONLY for striped backup. When urlsContainSas is true, omit WITH CREDENTIAL (SQL Server does not allow WITH CREDENTIAL for SAS credentials).</summary>
    public async Task<BackupFileInfo?> RestoreHeaderOnlyMultiAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, string? credentialName, bool urlsContainSas = false, CancellationToken ct = default)
    {
        if (blobUrls.Count == 0) return null;

        await using var conn = CreateConnection(server);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;

        var urlClauses = string.Join(", ", blobUrls.Select(u => $"URL = N'{TSql.EscapeLiteral(u)}'"));
        var withCredential = !urlsContainSas && !string.IsNullOrEmpty(credentialName)
            ? $" WITH CREDENTIAL = N'{TSql.EscapeLiteral(credentialName)}'"
            : "";
        cmd.CommandText = $"RESTORE HEADERONLY FROM {urlClauses}{withCredential}";

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
    }

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

    private static string Summarize(string statement)
        => statement[..Math.Min(80, statement.Length)].Trim();

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
