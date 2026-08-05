using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

public class BlobStorageService
{
    private readonly CredentialStore _credentialStore;

    public BlobStorageService(CredentialStore credentialStore)
    {
        _credentialStore = credentialStore;
    }

    private BlobContainerClient CreateClient(BlobContainerConfig config)
    {
        // An unsaved token wins, so Test Connection can try one without it having to be persisted
        // first (#12). Null for every ordinary operation, which falls through to the stored token.
        var sasToken = config.UnsavedSasToken ?? _credentialStore.GetSasToken(config);
        if (string.IsNullOrEmpty(sasToken))
            throw new InvalidOperationException(
                "No SAS token found. Please configure the SAS token for this container.");

        var baseUrl = config.ContainerUrl.TrimEnd('/');
        var cleanSas = sasToken.TrimStart('?');
        var separator = baseUrl.Contains('?') ? "&" : "?";
        var fullUri = new Uri($"{baseUrl}{separator}{cleanSas}");
        return new BlobContainerClient(fullUri);
    }

    public async Task<bool> VerifyConnectionAsync(BlobContainerConfig config, CancellationToken ct = default)
    {
        var client = CreateClient(config);
        await foreach (var _ in client.GetBlobsAsync(
            BlobTraits.None, BlobStates.None, prefix: null, cancellationToken: ct)
            .AsPages(pageSizeHint: 1))
        {
            break;
        }
        return true;
    }

    public async Task<List<BackupFileInfo>> ListBackupFilesAsync(
        BlobContainerConfig config, CancellationToken ct = default)
    {
        var client = CreateClient(config);
        var files = new List<BackupFileInfo>();

        await foreach (var blob in client.GetBlobsAsync(
            BlobTraits.Metadata, BlobStates.None, prefix: null, cancellationToken: ct))
        {
            var blobUrl = $"{config.ContainerUrl.TrimEnd('/')}/{blob.Name}";

            var file = new BackupFileInfo
            {
                BlobName = blob.Name,
                BlobUrl = blobUrl,
                Type = BackupType.Unknown,
                SizeBytes = blob.Properties.ContentLength ?? 0,
                LastModified = blob.Properties.LastModified ?? DateTimeOffset.MinValue
            };

            var pathParts = file.BlobName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var isFlatBlobName = pathParts.Length <= 1;
            var tryAgParsing = config.BackupSourceType == BackupSourceType.AvailabilityGroup
                || (config.BackupSourceType == BackupSourceType.Mixed && isFlatBlobName);

            if (tryAgParsing)
            {
                var agParsed = OlaAgFileNameParser.TryParse(blob.Name);
                if (agParsed != null)
                {
                    file.InferredServerName = agParsed.ServerDisplay;
                    file.InferredDatabaseName = agParsed.DatabaseName;
                    file.Type = agParsed.BackupType;
                    file.InferredSetId = agParsed.SetId;
                    file.IsAgDefaultNaming = true;
                    file.IsCopyOnly = agParsed.IsCopyOnly;
                }
            }

            if (!file.IsAgDefaultNaming)
            {
                var agPattern = config.AgPathPattern ?? "{BackupType}/{ServerName}/{DatabaseName}/{FileName}";
                if (pathParts.Length > 1 && config.BackupSourceType == BackupSourceType.AvailabilityGroup)
                {
                    // AG container with path: e.g. BackupType/ClusterName$AGName/DatabaseName/FileName
                    ParseBlobPath(file, agPattern);
                    TrySetInferredSetIdFromFileName(file);
                }
                else if (pathParts.Length > 1 && config.BackupSourceType == BackupSourceType.Mixed)
                {
                    ParseBlobPath(file, config.PathPattern);
                    if (file.Type == BackupType.Unknown || string.IsNullOrEmpty(file.InferredDatabaseName))
                    {
                        ParseBlobPath(file, agPattern);
                        TrySetInferredSetIdFromFileName(file);
                    }
                }
                else
                {
                    ParseBlobPath(file, config.PathPattern);
                }
            }

            if (file.Type == BackupType.Unknown)
                file.Type = InferBackupTypeFromExtension(blob.Name);

            if (!file.IsCopyOnly)
                file.IsCopyOnly = IsCopyOnlyFileName(file.FileName);

            files.Add(file);
        }

        return files.OrderBy(f => f.LastModified).ToList();
    }

    public ContainerSummary GetContainerSummary(List<BackupFileInfo> files)
    {
        return new ContainerSummary
        {
            TotalFiles = files.Count,
            FullBackups = files.Count(f => f.Type == BackupType.Full),
            DiffBackups = files.Count(f => f.Type == BackupType.Differential),
            LogBackups = files.Count(f => f.Type == BackupType.TransactionLog),
            UnknownFiles = files.Count(f => f.Type == BackupType.Unknown),
            TotalSizeBytes = files.Sum(f => f.SizeBytes),
            EarliestBackup = files.Count > 0 ? files.Min(f => f.LastModified) : null,
            LatestBackup = files.Count > 0 ? files.Max(f => f.LastModified) : null
        };
    }

    public List<string> GetDiscoveredDatabases(List<BackupFileInfo> files)
    {
        return files
            .Where(f => !string.IsNullOrEmpty(f.InferredDatabaseName))
            .Select(f => f.InferredDatabaseName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<string> GetDiscoveredServers(List<BackupFileInfo> files)
    {
        // Same formatter the filters match against, so the values offered in the dropdown and
        // the values compared against a set can never drift apart.
        return files
            .Select(f => f.ServerDisplay)
            .Where(s => s != null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    public ContainerSummary GetSetBasedSummary(List<BackupSet> sets)
    {
        return new ContainerSummary
        {
            TotalFiles = sets.Sum(s => s.FileCount),
            TotalSets = sets.Count,
            FullBackups = sets.Count(s => s.Type == BackupType.Full),
            DiffBackups = sets.Count(s => s.Type == BackupType.Differential),
            LogBackups = sets.Count(s => s.Type == BackupType.TransactionLog),
            UnknownFiles = sets.Count(s => s.Type == BackupType.Unknown),
            TotalSizeBytes = sets.Sum(s => s.TotalSizeBytes),
            EarliestBackup = sets.Count > 0 ? sets.Min(s => new DateTimeOffset(s.Timestamp)) : null,
            LatestBackup = sets.Count > 0 ? sets.Max(s => new DateTimeOffset(s.Timestamp)) : null
        };
    }

    /// <summary>
    /// Groups individual backup files into logical BackupSets, handling striped backups.
    /// </summary>
    public List<BackupSet> GroupIntoBackupSets(List<BackupFileInfo> files)
    {
        var groups = new Dictionary<string, List<BackupFileInfo>>();

        foreach (var file in files)
        {
            var (setId, _) = file.IsAgDefaultNaming && !string.IsNullOrEmpty(file.InferredSetId)
                ? (file.InferredSetId!, 0)
                : BackupSet.ParseFileName(file.FileName);

            // The key must identify ONE backup operation on ONE server. Type + database + setId
            // alone does not: two servers writing a same-named database to one container in the
            // same second collapse into a single "striped" set, which would generate one
            // RESTORE ... FROM URL = a, URL = b spanning both - and silently drop the second
            // server's backup from its own timeline. Log backups make this realistic: every
            // 5-15 minutes across two servers is hundreds of chances a day.
            //
            // The parent directory is included as well as server/instance because it survives an
            // unconfigured or partial path pattern: with no pattern InferredServerName is null on
            // both sides and would not separate them, whereas FULL/SRV01/Sales and
            // FULL/SRV02/Sales still differ. Every stripe of one backup shares both.
            var key = string.Join("|",
                file.Type,
                file.InferredServerName ?? "",
                file.InferredInstanceName ?? "",
                file.InferredDatabaseName ?? "",
                BlobDirectory(file.BlobName),
                // AG naming derives setId from the timestamp alone, so a copy-only and a regular
                // full taken in the same second would otherwise merge into one mixed set.
                file.IsCopyOnly ? "copyonly" : "",
                setId);

            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }
            list.Add(file);
        }

        var sets = new List<BackupSet>();
        foreach (var (key, groupFiles) in groups)
        {
            var first = groupFiles[0];
            var (setId, _) = BackupSet.ParseFileName(first.FileName);
            var timestamp = BackupSet.ParseTimestamp(setId) ?? first.LastModified.DateTime;

            sets.Add(new BackupSet
            {
                SetId = setId,
                Type = first.Type,
                Files = groupFiles.OrderBy(f => f.FileName).ToList(),
                Timestamp = timestamp,
                DatabaseName = first.InferredDatabaseName,
                ServerName = first.InferredServerName,
                InstanceName = first.InferredInstanceName,
                IsCopyOnly = first.IsCopyOnly
            });
        }

        return sets.OrderBy(s => s.Timestamp).ToList();
    }

    // A COPY_ONLY marker delimited by _ - . and preceded by a delimiter, so a database whose
    // name merely CONTAINS the words is not caught. Requiring a leading delimiter (rather than
    // allowing start-of-string) keeps a database literally named "Copy_Only_Archive" safe.
    // Deliberately not a bare substring match - see the ContainsDiffIndicator defect, where
    // "diff" matched DiffusionDb and classified its full backups as differentials.
    private static readonly Regex CopyOnlyRegex = new(
        @"[_\-.]copy[_\-]?only(?:$|[_\-.])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True when a backup filename carries a COPY_ONLY marker. Public because it is pure logic
    /// worth testing directly - the caller sits behind Azure IO.
    /// </summary>
    public static bool IsCopyOnlyFileName(string fileName)
        => !string.IsNullOrEmpty(fileName) && CopyOnlyRegex.IsMatch(fileName);

    /// <summary>Parent folder of a blob name, or empty for a flat name. Scopes set grouping.</summary>
    private static string BlobDirectory(string blobName)
    {
        var idx = blobName.LastIndexOf('/');
        return idx >= 0 ? blobName[..idx] : string.Empty;
    }

    /// <summary>
    /// The plain HTTPS URL of a blob, with no SAS token on it.
    ///
    /// This replaced BuildBlobUrlWithSas, which existed only to feed the two "copy HTTPS path"
    /// buttons and put a live credential on the Windows clipboard - where clipboard history
    /// (Win+V) and cloud clipboard sync can keep it long after the app has closed (#18). The
    /// token-free URL is what the generated scripts use anyway, since RESTORE FROM URL
    /// authenticates with the server-side credential rather than anything in the URL.
    /// </summary>
    public static string BuildBlobUrl(BlobContainerConfig config, string blobName)
        => $"{config.ContainerUrl.TrimEnd('/')}/{blobName}";

    private static void ParseBlobPath(BackupFileInfo file, string pathPattern)
    {
        var patternParts = pathPattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathParts = file.BlobName.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (pathParts.Length < patternParts.Length)
        {
            TryFallbackParsing(file, pathParts);
            return;
        }

        // When blob has more segments than pattern, collapse trailing segments into FileName
        for (int i = 0; i < patternParts.Length; i++)
        {
            var token = patternParts[i].Trim();
            string value;

            if (i == patternParts.Length - 1 && token.Equals("{FileName}", StringComparison.OrdinalIgnoreCase))
            {
                value = string.Join("/", pathParts.Skip(i));
            }
            else if (i < pathParts.Length)
            {
                value = pathParts[i];
            }
            else
            {
                continue;
            }

            if (token.Equals("{BackupType}", StringComparison.OrdinalIgnoreCase))
            {
                file.Type = ParseBackupTypeFromFolder(value);
            }
            else if (token.Equals("{ServerName}", StringComparison.OrdinalIgnoreCase))
            {
                file.InferredServerName = value;
                TrySplitClusterAndAg(value, file);
            }
            else if (token.Equals("{InstanceName}", StringComparison.OrdinalIgnoreCase))
            {
                file.InferredInstanceName = value;
            }
            else if (token.Equals("{DatabaseName}", StringComparison.OrdinalIgnoreCase))
            {
                file.InferredDatabaseName = value;
            }
            else if (token.Equals("{ClusterName}", StringComparison.OrdinalIgnoreCase))
            {
                file.InferredClusterName = value;
            }
            else if (token.Equals("{AgName}", StringComparison.OrdinalIgnoreCase))
            {
                file.InferredAgName = value;
            }
            else if (token.Equals("{ClusterName$AgName}", StringComparison.OrdinalIgnoreCase))
            {
                file.InferredServerName = value;
                TrySplitClusterAndAg(value, file);
            }
            else if (token.Equals("{ClusterName_AgName}", StringComparison.OrdinalIgnoreCase))
            {
                file.InferredServerName = value;
                TrySplitClusterAndAg(value, file);
            }
        }

        if (string.IsNullOrEmpty(file.InferredServerName) && !string.IsNullOrEmpty(file.InferredClusterName) && !string.IsNullOrEmpty(file.InferredAgName))
            file.InferredServerName = $"{file.InferredClusterName}${file.InferredAgName}";
    }

    /// <summary>
    /// Splits a path segment into cluster and AG name. Supports both "ClusterName$AgName" and "ClusterName_AgName".
    /// </summary>
    private static void TrySplitClusterAndAg(string serverSegment, BackupFileInfo file)
    {
        if (string.IsNullOrEmpty(serverSegment)) return;
        // Prefer $ then _ so "cluster_ag_name" doesn't split on first _
        var separator = serverSegment.IndexOf('$') >= 0 ? '$' : '_';
        var idx = serverSegment.IndexOf(separator);
        if (idx < 0) return;
        file.InferredClusterName = serverSegment[..idx];
        file.InferredAgName = serverSegment[(idx + 1)..];
    }

    private static void TryFallbackParsing(BackupFileInfo file, string[] pathParts)
    {
        // Try to infer what we can from whatever structure exists
        if (pathParts.Length >= 2)
        {
            var firstFolder = pathParts[0].ToUpperInvariant();
            var parsedType = ParseBackupTypeFromFolder(firstFolder);
            if (parsedType != BackupType.Unknown)
            {
                file.Type = parsedType;
                // If there are 3+ parts: type/something/filename - the middle might be db name
                if (pathParts.Length >= 3)
                    file.InferredDatabaseName = pathParts[^2]; // second-to-last
            }
        }
    }

    private static BackupType ParseBackupTypeFromFolder(string folderName)
    {
        var upper = folderName.ToUpperInvariant();
        return upper switch
        {
            "FULL" => BackupType.Full,
            "DIFF" or "DIFFERENTIAL" => BackupType.Differential,
            "LOG" or "TLOG" or "TRN" or "TRANSACTIONLOG" => BackupType.TransactionLog,
            _ => BackupType.Unknown
        };
    }

    /// <summary>
    /// Last-resort type inference from the filename, reached only when the path structure did not
    /// say what a backup is.
    ///
    /// Getting this wrong is not cosmetic. A log misread as a full enters the fulls collection in
    /// BackupChainBuilder and becomes a chain root, so the timeline offers a log file as a
    /// restorable Full point and every earlier log is dropped from the chain (#44). A full misread
    /// as a differential never enters that collection at all, and if it is the only full in the
    /// container the database gets no restore points whatsoever (#45).
    /// </summary>
    internal static BackupType InferBackupTypeFromExtension(string blobName)
    {
        var name = blobName.ToLowerInvariant();

        if (name.EndsWith(".trn") || name.EndsWith(".log"))
            return BackupType.TransactionLog;

        if (name.EndsWith(".diff"))
            return BackupType.Differential;

        if (name.EndsWith(".bak") || name.EndsWith(".bkp"))
        {
            // Indicators are read from the filename only, never the folders above it - those are
            // the primary path's job, and a container called "logs" should not retype every file
            // underneath it.
            var fileName = name[(name.LastIndexOf('/') + 1)..];

            if (ContainsDiffIndicator(fileName))
                return BackupType.Differential;

            // A log written as .bak used to land here as Full. Ola and maintenance plans both emit
            // .trn so this needs a hand-rolled job, but the failure was bad enough to be worth
            // catching (#44).
            if (ContainsLogIndicator(fileName))
                return BackupType.TransactionLog;

            return BackupType.Full;
        }

        return BackupType.Unknown;
    }

    // Delimited, not a bare substring. The old version tested name.Contains("diff"), which made
    // every other entry in its list redundant and classified DiffusionDb's FULL backups as
    // differentials (#45). Same anchoring as CopyOnlyRegex above.
    private static readonly Regex DiffIndicatorRegex = new(
        @"(?:^|[_\-.])(?:diff|differential)(?:$|[_\-.])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Deliberately delimited for the same reason, and more sharply here: a bare "log" substring
    // would retype CatalogDb, BlogDb and DialogDb backups as transaction logs.
    private static readonly Regex LogIndicatorRegex = new(
        @"(?:^|[_\-.])(?:log|tlog|trn|translog|transactionlog)(?:$|[_\-.])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when a filename carries a delimited differential marker. Public for testing.</summary>
    public static bool ContainsDiffIndicator(string fileName)
        => !string.IsNullOrEmpty(fileName) && DiffIndicatorRegex.IsMatch(fileName);

    /// <summary>
    /// True when a filename carries a delimited transaction-log marker. Public for testing.
    ///
    /// Known limitation: a database actually named "Log" or "Diff" would match on its own name.
    /// Nothing in a filename can settle that, and the alternative - a bare substring - is the bug
    /// being fixed. Path-based typing takes precedence anyway, so this only bites a flat container
    /// holding a database with one of those names.
    /// </summary>
    public static bool ContainsLogIndicator(string fileName)
        => !string.IsNullOrEmpty(fileName) && LogIndicatorRegex.IsMatch(fileName);

    /// <summary>
    /// For path-based AG files, extract backup set id from the filename segment (e.g. 20260226_200032_1.bak → 20260226_200032).
    /// </summary>
    private static void TrySetInferredSetIdFromFileName(BackupFileInfo file)
    {
        var (setId, _) = BackupSet.ParseFileName(file.FileName);
        if (BackupSet.ParseTimestamp(setId) != null)
            file.InferredSetId = setId;
    }
}

public class ContainerSummary
{
    public int TotalFiles { get; set; }
    public int TotalSets { get; set; }
    public int FullBackups { get; set; }
    public int DiffBackups { get; set; }
    public int LogBackups { get; set; }
    public int UnknownFiles { get; set; }
    public long TotalSizeBytes { get; set; }
    public DateTimeOffset? EarliestBackup { get; set; }
    public DateTimeOffset? LatestBackup { get; set; }

    public string TotalSizeDisplay => ByteSize.Format(TotalSizeBytes);
}
