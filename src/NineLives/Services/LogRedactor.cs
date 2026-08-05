using System.Text.RegularExpressions;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Strips secrets out of anything on its way to the log file (#40).
///
/// This runs at the logging boundary, not at the call sites, and that is the whole design. Asking
/// every caller to remember to redact means it takes exactly one forgetful caller - or one new one
/// - to write a live SAS token into a file that then gets attached to a bug report. Everything
/// passes through here, so a new call site is safe by default.
///
/// The rule is deliberately blunt: if something looks like it might be a secret, it goes. A log
/// line that is over-redacted is a mild annoyance. A log line with a working SAS token in it is a
/// credential leak with a filename.
/// </summary>
public static class LogRedactor
{
    private const string Mask = "[redacted]";

    // The SAS signature - the part that actually grants access. Matched first and on its own so a
    // URL keeps enough shape to stay useful for diagnosis.
    private static readonly Regex SasSignature = new(
        @"(?<key>\b(?:sig|sv|se|st|sp|sr|si|skoid|sktid|ske|skt|sks|spr|srt|ss)=)(?<value>[^&\s'"";]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // T-SQL: CREATE/ALTER CREDENTIAL ... SECRET = 'the token'
    private static readonly Regex TsqlSecret = new(
        @"(?<key>\bSECRET\s*=\s*)(?<quote>N?')(?:[^']|'')*'",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Connection-string style: Password=..., Pwd=...
    private static readonly Regex ConnectionPassword = new(
        @"(?<key>\b(?:password|pwd)\s*=\s*)(?<value>[^;\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns the text with anything secret-shaped replaced. Never throws.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        try
        {
            var result = TsqlSecret.Replace(text, m => $"{m.Groups["key"].Value}{m.Groups["quote"].Value}{Mask}'");
            result = SasSignature.Replace(result, m => $"{m.Groups["key"].Value}{Mask}");
            result = ConnectionPassword.Replace(result, m => $"{m.Groups["key"].Value}{Mask}");
            return result;
        }
        catch
        {
            // A redaction failure must never let the raw text through.
            return Mask;
        }
    }
}
