using System.Text;
using System.Text.Json;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Who a token was issued to, read from the token itself (#29).
///
/// Azure answers a data-plane permission failure with 403 AuthorizationPermissionMismatch and
/// nothing else. That is one message for several very different problems - the role is assigned but
/// on the management plane, the role is on the wrong scope, or the token is for a different account
/// or tenant than the one that was granted access. Being told WHICH identity was refused is usually
/// enough to tell them apart, and there is no other way to find out from inside the app.
///
/// The claims are read WITHOUT validating the signature, deliberately. This app is not the resource
/// server - it is not deciding whether to trust the token, it is repeating back who Azure said it
/// belongs to so a human can spot that it is the wrong person. Azure has already validated it, and
/// the only thing done with the result is display.
/// </summary>
internal static class EntraIdentity
{
    /// <summary>
    /// A short description of the account a token belongs to, or null if it cannot be read.
    ///
    /// Never returns any part of the token itself. A JWT bearer token is a live credential for as
    /// long as it lasts, and this text goes on screen and into the log.
    /// </summary>
    internal static string? Describe(string accessToken)
    {
        var claims = ReadClaims(accessToken);
        if (claims == null) return null;

        // A user token names the person; a managed identity or service principal has no user at
        // all, only an object id - which is still the thing to check a role assignment against.
        var who = First(claims, "upn", "preferred_username", "unique_name", "email")
            ?? First(claims, "appid", "azp")
            ?? First(claims, "oid");

        var tenant = First(claims, "tid");

        if (who == null && tenant == null) return null;
        if (who == null) return $"tenant {tenant}";
        return tenant == null ? who : $"{who} (tenant {tenant})";
    }

    private static string? First(Dictionary<string, string> claims, params string[] names)
    {
        foreach (var name in names)
            if (claims.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        return null;
    }

    /// <summary>
    /// The payload segment's claims. Anything malformed comes back null rather than throwing - this
    /// only ever runs to improve an error message, and failing to improve one must not replace it
    /// with a different error.
    /// </summary>
    private static Dictionary<string, string>? ReadClaims(string accessToken)
    {
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2) return null;

            using var document = JsonDocument.Parse(FromBase64Url(parts[1]));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            var claims = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String)
                    claims[property.Name] = property.Value.GetString() ?? string.Empty;

            return claims;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Base64url, which is base64 with two characters swapped and the padding dropped.</summary>
    private static byte[] FromBase64Url(string value)
    {
        var padded = new StringBuilder(value.Replace('-', '+').Replace('_', '/'));
        while (padded.Length % 4 != 0) padded.Append('=');
        return Convert.FromBase64String(padded.ToString());
    }
}
