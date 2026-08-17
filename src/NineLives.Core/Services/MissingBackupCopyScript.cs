using System.Text;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// The PowerShell that brings backups the container is missing into it (#451).
///
/// Runs on the SOURCE machine, because that is where the files are - a local or cluster disk this
/// app has no route to. So it is a script somebody takes away and runs there, not something the
/// app can do on their behalf.
///
/// It NEVER carries the credential. The whole point of the container's secret living in Windows
/// Credential Manager is defeated by writing it into a .ps1 that then sits in a folder on a
/// production server, and SECURITY.md states outright that a generated script contains none - a
/// claim that has to stay true of every generator, not just the ones written when it was made.
/// The credential arrives as a mandatory parameter at run time instead, which is also the form
/// somebody can wire into a scheduled task without it touching disk.
/// </summary>
public static class MissingBackupCopyScript
{
    /// <summary>
    /// One script per location, because a location is one folder and one command shape. Two
    /// folders needing two runs is honest about what is happening; concatenating them into one
    /// script with interleaved paths is what produces a half-finished copy nobody can unpick.
    /// </summary>
    public static string Build(MissingLocation location, BlobContainerConfig container)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<#");
        sb.AppendLine("    Nine Lives - copy backups into the container");
        sb.AppendLine();
        sb.AppendLine($"    Run this ON the machine that holds {location.Folder}.");
        sb.AppendLine();
        sb.AppendLine($"    {location.Summary} that this container does not have,");
        sb.AppendLine($"    taken between {location.Earliest:yyyy-MM-dd HH:mm} and {location.Latest:yyyy-MM-dd HH:mm}.");
        sb.AppendLine($"    {location.FileCount} file(s), {location.SizeDisplay}.");
        sb.AppendLine();
        sb.AppendLine("    The credential is a parameter, not a value in this file - so this script");
        sb.AppendLine("    is safe to save, review and hand over. Supply it when you run it.");
        sb.AppendLine("#>");
        sb.AppendLine();

        if (container.IsS3) AppendS3(sb, location, container);
        else AppendAzure(sb, location, container);

        return sb.ToString();
    }

    private static void AppendAzure(
        StringBuilder sb, MissingLocation location, BlobContainerConfig container)
    {
        sb.AppendLine("param(");
        sb.AppendLine("    # The container's SAS token, including the leading '?'.");
        sb.AppendLine("    [Parameter(Mandatory = $true)]");
        sb.AppendLine("    [string] $Sas");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine();
        sb.AppendLine($"$container = '{container.ContainerUrl.TrimEnd('/')}'");
        sb.AppendLine();
        sb.AppendLine("# azcopy is the fast path and resumes a part-finished copy. If it is not on this");
        sb.AppendLine("# machine, the Az.Storage fallback below does the same job more slowly.");
        sb.AppendLine("$azcopy = Get-Command azcopy -ErrorAction SilentlyContinue");
        sb.AppendLine();

        AppendFileList(sb, location);

        sb.AppendLine("$failed = @()");
        sb.AppendLine();
        sb.AppendLine("foreach ($file in $files) {");
        sb.AppendLine("    if (-not (Test-Path -LiteralPath $file)) {");
        sb.AppendLine("        Write-Warning \"Not on disk any more: $file\"");
        sb.AppendLine("        $failed += $file");
        sb.AppendLine("        continue");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    $name = Split-Path -Leaf $file");
        sb.AppendLine("    Write-Host \"Copying $name...\"");
        sb.AppendLine();
        sb.AppendLine("    try {");
        sb.AppendLine("        if ($azcopy) {");
        sb.AppendLine("            & azcopy copy $file \"$container/$name$Sas\" --overwrite=ifSourceNewer | Out-Null");
        sb.AppendLine("            if ($LASTEXITCODE -ne 0) { throw \"azcopy exited $LASTEXITCODE\" }");
        sb.AppendLine("        }");
        sb.AppendLine("        else {");
        sb.AppendLine("            $ctx = New-AzStorageContext -BlobEndpoint ($container -replace '/[^/]+$', '') -SasToken $Sas");
        sb.AppendLine("            $containerName = Split-Path -Leaf $container");
        sb.AppendLine("            Set-AzStorageBlobContent -File $file -Container $containerName -Blob $name -Context $ctx -Force | Out-Null");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("    catch {");
        sb.AppendLine("        Write-Warning \"Failed: $name - $_\"");
        sb.AppendLine("        $failed += $file");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        AppendEnding(sb);
    }

    private static void AppendS3(
        StringBuilder sb, MissingLocation location, BlobContainerConfig container)
    {
        var url = S3Url.TryParse(container.ContainerUrl.TrimEnd('/'));
        var bucket = url?.Bucket ?? "BUCKET";
        var prefix = string.IsNullOrWhiteSpace(url?.BasePrefix) ? "" : url!.BasePrefix.Trim('/') + "/";
        var endpoint = url?.Authority ?? "ENDPOINT";

        sb.AppendLine("param(");
        sb.AppendLine("    [Parameter(Mandatory = $true)] [string] $AccessKeyId,");
        sb.AppendLine("    [Parameter(Mandatory = $true)] [string] $SecretAccessKey");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine();
        sb.AppendLine("# Set for this process only - nothing is written to the profile or the machine.");
        sb.AppendLine("$env:AWS_ACCESS_KEY_ID = $AccessKeyId");
        sb.AppendLine("$env:AWS_SECRET_ACCESS_KEY = $SecretAccessKey");

        if (!string.IsNullOrWhiteSpace(container.S3Region))
            sb.AppendLine($"$env:AWS_DEFAULT_REGION = '{container.S3Region}'");

        sb.AppendLine();
        sb.AppendLine($"$bucket = '{bucket}'");
        sb.AppendLine($"$prefix = '{prefix}'");
        sb.AppendLine($"$endpoint = 'https://{endpoint}'");
        sb.AppendLine();

        AppendFileList(sb, location);

        sb.AppendLine("$failed = @()");
        sb.AppendLine();
        sb.AppendLine("foreach ($file in $files) {");
        sb.AppendLine("    if (-not (Test-Path -LiteralPath $file)) {");
        sb.AppendLine("        Write-Warning \"Not on disk any more: $file\"");
        sb.AppendLine("        $failed += $file");
        sb.AppendLine("        continue");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    $name = Split-Path -Leaf $file");
        sb.AppendLine("    Write-Host \"Copying $name...\"");
        sb.AppendLine();
        sb.AppendLine("    & aws s3 cp $file \"s3://$bucket/$prefix$name\" --endpoint-url $endpoint");
        sb.AppendLine("    if ($LASTEXITCODE -ne 0) {");
        sb.AppendLine("        Write-Warning \"Failed: $name\"");
        sb.AppendLine("        $failed += $file");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        AppendEnding(sb);
    }

    /// <summary>
    /// The files, named one by one rather than as a wildcard over the folder.
    ///
    /// A wildcard would copy whatever else is in there - other databases' backups, an unrelated
    /// job's output, files somebody is midway through writing. This list is the answer to a
    /// specific question (what is this chain missing), and copying anything else is both a
    /// surprise and, on a metered egress link, a bill.
    /// </summary>
    private static void AppendFileList(StringBuilder sb, MissingLocation location)
    {
        sb.AppendLine("# Named individually, not a wildcard: only what this chain is actually missing.");
        sb.AppendLine("$files = @(");

        var paths = location.Backups.SelectMany(b => b.Files).ToList();
        for (int i = 0; i < paths.Count; i++)
        {
            var comma = i < paths.Count - 1 ? "," : "";
            sb.AppendLine($"    '{paths[i].Replace("'", "''")}'{comma}");
        }

        sb.AppendLine(")");
        sb.AppendLine();
    }

    private static void AppendEnding(StringBuilder sb)
    {
        sb.AppendLine("if ($failed.Count -gt 0) {");
        sb.AppendLine("    Write-Host \"\"");
        sb.AppendLine("    Write-Warning \"$($failed.Count) file(s) did not copy:\"");
        sb.AppendLine("    $failed | ForEach-Object { Write-Warning \"  $_\" }");
        sb.AppendLine("    Write-Host \"\"");
        sb.AppendLine("    Write-Host 'Rescan in Nine Lives to see what did arrive.'");
        sb.AppendLine("    exit 1");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Write-Host \"\"");
        sb.AppendLine("Write-Host 'Done. Rescan the container in Nine Lives to pick them up.'");
    }
}
