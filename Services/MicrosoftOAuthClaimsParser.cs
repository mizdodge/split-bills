using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace Splitbill.Services;

public static class MicrosoftOAuthClaimsParser
{
    public const string OidClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    public const string TenantIdClaimType = "http://schemas.microsoft.com/identity/claims/tenantid";

    public static void PopulateClaimsFromIdToken(OAuthCreatingTicketContext context)
    {
        if (context.Identity is null) return;

        if (context.TokenResponse.Response?.RootElement.TryGetProperty("id_token", out var idTokenProp) == true &&
            idTokenProp.ValueKind == JsonValueKind.String)
        {
            var idToken = idTokenProp.GetString();
            if (!string.IsNullOrWhiteSpace(idToken))
            {
                PopulateClaimsFromJwtPayload(idToken, context.Identity);
            }
        }
    }

    public static void PopulateClaimsFromJwtPayload(string jwt, ClaimsIdentity identity)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return;

        try
        {
            var base64 = parts[1].Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            var jsonBytes = Convert.FromBase64String(base64);
            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;

            AddClaimIfMissing(identity, OidClaimType, root, "oid");
            AddClaimIfMissing(identity, "oid", root, "oid");
            AddClaimIfMissing(identity, ClaimTypes.NameIdentifier, root, "sub");
            AddClaimIfMissing(identity, "sub", root, "sub");
            AddClaimIfMissing(identity, TenantIdClaimType, root, "tid");
            AddClaimIfMissing(identity, "tid", root, "tid");
            AddClaimIfMissing(identity, "preferred_username", root, "preferred_username");
            AddClaimIfMissing(identity, ClaimTypes.Name, root, "name");
            AddClaimIfMissing(identity, ClaimTypes.Upn, root, "upn");

            if (!identity.HasClaim(c => c.Type == ClaimTypes.Email))
            {
                string? email = null;
                if (root.TryGetProperty("email", out var ep) && ep.ValueKind == JsonValueKind.String)
                    email = ep.GetString();
                else if (root.TryGetProperty("preferred_username", out var pu) && pu.ValueKind == JsonValueKind.String)
                    email = pu.GetString();
                else if (root.TryGetProperty("upn", out var up) && up.ValueKind == JsonValueKind.String)
                    email = up.GetString();

                if (!string.IsNullOrWhiteSpace(email))
                {
                    identity.AddClaim(new Claim(ClaimTypes.Email, email));
                }
            }
        }
        catch
        {
            // Proceed with existing claims if decoding fails
        }
    }

    private static void AddClaimIfMissing(ClaimsIdentity identity, string claimType, JsonElement root, string propertyName)
    {
        if (identity.HasClaim(c => c.Type == claimType)) return;
        if (root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var val = prop.GetString();
            if (!string.IsNullOrWhiteSpace(val))
            {
                identity.AddClaim(new Claim(claimType, val));
            }
        }
    }
}
