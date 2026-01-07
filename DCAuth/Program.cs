using System.Net;
using System.DirectoryServices.Protocols;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Helper: when running in Production prefer environment variables for configuration
static string? GetConfigurationValue(IConfiguration cfg, string key, IHostEnvironment env)
{
    if (env.IsProduction())
    {
        // Try common env var conventions: colon -> double underscore, or upper with underscore
        var envKey1 = key.Replace(":", "__");
        var envKey2 = key.Replace(":", "_").ToUpperInvariant();
        var v = Environment.GetEnvironmentVariable(envKey1);
        if (string.IsNullOrEmpty(v)) v = Environment.GetEnvironmentVariable(envKey2);
        if (!string.IsNullOrEmpty(v)) return v;
    }

    return cfg[key];
}

// New endpoint: checkAuth
// Accepts JSON body: { "username": "...", "password": "...", "domain": "..." }
// Returns: { "authenticated": true/false, "token": "..." }
app.MapPost("/checkAuth", async (Credentials creds, IConfiguration config) =>
{
    if (creds is null)
        return Results.BadRequest(new { error = "missing_credentials" });

    var dcUrl = GetConfigurationValue(config, "DomainController:Url", app.Environment);
    if (string.IsNullOrWhiteSpace(dcUrl))
    {
        return Results.Problem(detail: "Domain controller URL not configured", statusCode: 500);
    }

    // parse URL - support ldap://, ldaps:// or plain hostname
    string host = dcUrl;
    int port = 389;
    bool useSsl = false;

    if (Uri.TryCreate(dcUrl, UriKind.Absolute, out var uri))
    {
        host = uri.Host;
        if (!uri.IsDefaultPort)
            port = uri.Port;
        else
            port = (uri.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase)) ? 636 : 389;
        useSsl = uri.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase);
    }

    // Prefer domain from request; fall back to configured domain if request does not contain it
    var domainFromRequest = creds.Domain;
    var domain = !string.IsNullOrWhiteSpace(domainFromRequest) ? domainFromRequest : GetConfigurationValue(config, "DomainController:Domain", app.Environment);

    // Determine credential for bind in a way compatible with Negotiate/Basic
    // Normalize incoming username into components when possible.
    var rawUsername = creds.Username ?? string.Empty;
    NetworkCredential credential;

    if (rawUsername.Contains("\\"))
    {
        var parts = rawUsername.Split('\\', 2);
        var domPart = parts[0];
        var userPart = parts.Length > 1 ? parts[1] : string.Empty;
        credential = new NetworkCredential(userPart, creds.Password ?? string.Empty, domPart);
    }
    else if (rawUsername.Contains("@"))
    {
        // UPN form - supply as username; domain left empty
        credential = new NetworkCredential(rawUsername, creds.Password ?? string.Empty);
    }
    else if (!string.IsNullOrWhiteSpace(domain))
    {
        // use provided domain as domain component (NetworkCredential will format correctly for Negotiate)
        credential = new NetworkCredential(rawUsername, creds.Password ?? string.Empty, domain);
    }
    else
    {
        credential = new NetworkCredential(rawUsername, creds.Password ?? string.Empty);
    }

    try
    {
        var identifier = new LdapDirectoryIdentifier(host, port, false, false);
        // Try bind with reasonable defaults. Many AD installations require "strong authentication" for simple binds over plaintext;
        // prefer Negotiate (Kerberos/NTLM) where possible, but allow Basic over LDAPS.
        try
        {
            // Primary attempt: use Negotiate for non-SSL, Basic for LDAPS
            using var connection = new LdapConnection(identifier)
            {
                AuthType = useSsl ? AuthType.Basic : AuthType.Negotiate,
                Timeout = TimeSpan.FromSeconds(5)
            };

            connection.SessionOptions.ProtocolVersion = 3;
            if (useSsl)
                connection.SessionOptions.SecureSocketLayer = true;

            connection.Credential = credential;

            // Perform bind on a background thread (Bind is synchronous)
            await Task.Run(() => connection.Bind());
        }
        catch (DirectoryOperationException doe) when (doe.Message?.IndexOf("Strong authentication", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            // Server requires strong authentication for the attempted method. Try Negotiate explicitly as a fallback,
            // then NTLM if Negotiate also fails.
            var tried = new List<string> { useSsl ? "Basic" : "Negotiate" };
            try
            {
                using var altConn = new LdapConnection(new LdapDirectoryIdentifier(host, port, false, false))
                {
                    AuthType = AuthType.Negotiate,
                    Timeout = TimeSpan.FromSeconds(5)
                };

                altConn.SessionOptions.ProtocolVersion = 3;
                if (useSsl)
                    altConn.SessionOptions.SecureSocketLayer = true;

                altConn.Credential = credential;

                await Task.Run(() => altConn.Bind());
                tried.Add("Negotiate");
            }
            catch (DirectoryOperationException)
            {
                // Try NTLM as a last resort
                try
                {
                    using var ntlmConn = new LdapConnection(new LdapDirectoryIdentifier(host, port, false, false))
                    {
                        AuthType = AuthType.Ntlm,
                        Timeout = TimeSpan.FromSeconds(5)
                    };

                    ntlmConn.SessionOptions.ProtocolVersion = 3;
                    if (useSsl)
                        ntlmConn.SessionOptions.SecureSocketLayer = true;

                    ntlmConn.Credential = credential;

                    await Task.Run(() => ntlmConn.Bind());
                    tried.Add("NTLM");
                }
                catch (DirectoryOperationException)
                {
                    // all attempts failed — rethrow original to outer catch
                    throw;
                }
            }
        }

        // If bind succeeded, authenticated -> generate JWT
        var jwtKey = GetConfigurationValue(config, "Jwt:Key", app.Environment);
        var jwtIssuer = GetConfigurationValue(config, "Jwt:Issuer", app.Environment);
        var jwtAudience = GetConfigurationValue(config, "Jwt:Audience", app.Environment);
        var expiresMinutes = 60;
        var expiresCfg = GetConfigurationValue(config, "Jwt:ExpiresMinutes", app.Environment);
        if (!string.IsNullOrWhiteSpace(expiresCfg) && int.TryParse(expiresCfg, out var m))
            expiresMinutes = m;

        if (string.IsNullOrWhiteSpace(jwtKey))
        {
            return Results.Problem(detail: "JWT signing key not configured (Jwt:Key)", statusCode: 500);
        }

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var signingCreds = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);


        var claims = new List<Claim>
            {
                new Claim("IssuedAt", DateTime.Now.ToString(), ClaimValueTypes.Integer64),
            };

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: string.IsNullOrWhiteSpace(jwtIssuer) ? null : jwtIssuer,
            audience: string.IsNullOrWhiteSpace(jwtAudience) ? null : jwtAudience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(expiresMinutes),
            signingCredentials: signingCreds
        );

        token.Payload.Add("sub", credential.UserName);
        token.Payload.Add("iat", ((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds());

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return Results.Ok(new { authenticated = true, token = tokenString });
    }
    catch (LdapException)
    {
        // authentication failed or other LDAP error
        return Results.Ok(new { authenticated = false });
    }
    catch (Exception ex)
    {
        // unexpected error
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
})
.Accepts<Credentials>("application/json");

app.Run();


internal record Credentials(string Username, string Password, string? Domain);
