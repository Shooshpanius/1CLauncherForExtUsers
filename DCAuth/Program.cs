using System.Net;
using Novell.Directory.Ldap;
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
        return Results.Problem(detail: "Domain controller URL not configured", statusCode: 500);

    // parse URL
    if (!Uri.TryCreate(dcUrl, UriKind.Absolute, out var uri))
        return Results.Problem(detail: "Domain controller URL invalid", statusCode: 500);

    var host = uri.Host;
    var port = uri.IsDefaultPort ? (uri.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase) ? 636 : 389) : uri.Port;
    var useSsl = uri.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase);

    var domainFromRequest = creds.Domain;
    var domain = !string.IsNullOrWhiteSpace(domainFromRequest) ? domainFromRequest : GetConfigurationValue(config, "DomainController:Domain", app.Environment);

    // Normalize username
    var rawUsername = creds.Username ?? string.Empty;
    var username = rawUsername;
    var bindDomain = domain;

    if (rawUsername.Contains("\\"))
    {
        var parts = rawUsername.Split('\\', 2);
        bindDomain = parts[0];
        username = parts.Length > 1 ? parts[1] : string.Empty;
    }

    // If UPN provided (user@domain), use as-is
    if (username.Contains("@"))
    {
        bindDomain = null;
    }

    var password = creds.Password ?? string.Empty;

    try
    {
        // local helper using Novell.Directory.Ldap synchronous API wrapped in Task.Run
        async Task<bool> IsUserExistAsyncLocal(string h, int p, bool initialSsl, string? dom, string user, string pwd)
        {
            string bindName = !string.IsNullOrWhiteSpace(user) && user.Contains("@")
                ? user
                : (!string.IsNullOrWhiteSpace(dom) ? $"{dom}\\{user}" : user);

            // Primary attempt
            try
            {
                using var conn = new LdapConnection();
                if (initialSsl)
                    conn.SecureSocketLayer = true;

                await Task.Run(() =>
                {
                    conn.Connect(h, p);
                    conn.Bind(bindName, pwd);
                });

                try { if (conn.Connected) conn.Disconnect(); } catch { }
                return true;
            }
            catch (LdapException ex)
            {
                var msg = ex.Message ?? string.Empty;
                if (msg.IndexOf("Strong authentication", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("Error initializing SSL/TLS", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Try StartTLS
                    try
                    {
                        using var startConn = new LdapConnection();
                        await Task.Run(() =>
                        {
                            startConn.Connect(h, p);
                            startConn.StartTls();
                            startConn.Bind(bindName, pwd);
                        });

                        try { if (startConn.Connected) startConn.Disconnect(); } catch { }
                        return true;
                    }
                    catch { }

                    // Fallback to LDAPS
                    try
                    {
                        using var sslConn = new LdapConnection { SecureSocketLayer = true };
                        await Task.Run(() =>
                        {
                            sslConn.Connect(h, 636);
                            sslConn.Bind(bindName, pwd);
                        });

                        try { if (sslConn.Connected) sslConn.Disconnect(); } catch { }
                        return true;
                    }
                    catch { }
                }

                return false;
            }
        }

        var ok = await IsUserExistAsyncLocal(host, port, useSsl, bindDomain, username, password);
        if (!ok)
            return Results.Ok(new { authenticated = false });

        // generate JWT same as sample
        var jwtKey = GetConfigurationValue(config, "Jwt:Key", app.Environment);
        var jwtIssuer = GetConfigurationValue(config, "Jwt:Issuer", app.Environment);
        var jwtAudience = GetConfigurationValue(config, "Jwt:Audience", app.Environment);
        var expiresMinutes = 60;
        var expiresCfg = GetConfigurationValue(config, "Jwt:ExpiresMinutes", app.Environment);
        if (!string.IsNullOrWhiteSpace(expiresCfg) && int.TryParse(expiresCfg, out var m)) expiresMinutes = m;

        if (string.IsNullOrWhiteSpace(jwtKey))
            return Results.Problem(detail: "JWT signing key not configured (Jwt:Key)", statusCode: 500);

        var claims = new List<Claim> { new Claim("IssuedAt", DateTime.Now.ToString(), ClaimValueTypes.Integer64) };
        var signingCreds = new SigningCredentials(new SymmetricSecurityKey(Encoding.ASCII.GetBytes(jwtKey)), SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            notBefore: DateTime.UtcNow,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expiresMinutes),
            signingCredentials: signingCreds
        );

        var subject = !string.IsNullOrWhiteSpace(bindDomain) && !username.Contains("@") ? $"{bindDomain}\\{username}" : username;
        token.Payload.Add("sub", subject);
        token.Payload.Add("iat", ((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds());

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
        return Results.Ok(new { authenticated = true, token = tokenString });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
})
.Accepts<Credentials>("application/json");

app.Run();


internal record Credentials(string Username, string Password, string? Domain);
