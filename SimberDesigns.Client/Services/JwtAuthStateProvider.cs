using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace SimberDesigns.Client.Services;

public sealed class JwtAuthStateProvider(IJSRuntime js) : AuthenticationStateProvider
{
    public const string TokenKey = "simber.authToken";

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await js.InvokeAsync<string>("localStorage.getItem", TokenKey);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }

        var identity = new ClaimsIdentity(ParseClaims(token), "jwt");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public async Task SignInAsync(string token)
    {
        await js.InvokeVoidAsync("localStorage.setItem", TokenKey, token);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public async Task SignOutAsync()
    {
        await js.InvokeVoidAsync("localStorage.removeItem", TokenKey);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public static IEnumerable<Claim> ParseClaims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return [];
        }

        var json = JsonDocument.Parse(Pad(parts[1]));
        var claims = new List<Claim>();
        foreach (var property in json.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    claims.Add(new Claim(MapClaimType(property.Name), item.ToString()));
                }
            }
            else
            {
                claims.Add(new Claim(MapClaimType(property.Name), property.Value.ToString()));
            }
        }

        return claims;
    }

    private static string MapClaimType(string type) => type switch
    {
        "role" or "roles" or "http://schemas.microsoft.com/ws/2008/06/identity/claims/role" => ClaimTypes.Role,
        "sub" or "nameid" => ClaimTypes.NameIdentifier,
        "email" => ClaimTypes.Email,
        "unique_name" or "name" => ClaimTypes.Name,
        _ => type
    };

    private static byte[] Pad(string base64)
    {
        var payload = base64.Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "="; break;
        }

        return Convert.FromBase64String(payload);
    }
}

public sealed class BearerTokenHandler(IJSRuntime js) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await js.InvokeAsync<string>("localStorage.getItem", JwtAuthStateProvider.TokenKey);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
