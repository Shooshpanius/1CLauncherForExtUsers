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

// New endpoint: checkAuth
// Accepts JSON body: { "username": "...", "password": "...", "domain": "..." }
// Returns: { "authenticated": true/false, "token": "..." }
app.MapPost("/checkAuth", async (Credentials creds, IConfiguration config) =>
{
    if (creds is null)
        return Results.BadRequest(new { error = "missing_credentials" });

    var dcUrl = config["DomainController:Url"];
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

    try
    {
        var identifier = new LdapDirectoryIdentifier(host, port, false, false);
        using var connection = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(5)
        };

        if (useSsl)
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }

        // Prefer domain from request; fall back to configured domain if request does not contain it
        var domainFromRequest = creds.Domain;
        var domain = !string.IsNullOrWhiteSpace(domainFromRequest) ? domainFromRequest : config["DomainController:Domain"];

        var usernameForBind = creds.Username ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(domain) && !usernameForBind.Contains("\\") && !usernameForBind.Contains("@"))
        {
            usernameForBind = domain + "\\" + usernameForBind;
        }

        var credential = new NetworkCredential(usernameForBind, creds.Password ?? string.Empty);

        // Perform bind on a background thread (Bind is synchronous)
        await Task.Run(() => connection.Bind(credential));

        // If bind succeeded, authenticated -> generate JWT
        var jwtKey = config["Jwt:Key"];
        var jwtIssuer = config["Jwt:Issuer"];
        var jwtAudience = config["Jwt:Audience"];
        var expiresMinutes = 60;
        if (!string.IsNullOrWhiteSpace(config["Jwt:ExpiresMinutes"]) && int.TryParse(config["Jwt:ExpiresMinutes"], out var m))
            expiresMinutes = m;

        if (string.IsNullOrWhiteSpace(jwtKey))
        {
            return Results.Problem(detail: "JWT signing key not configured (Jwt:Key)", statusCode: 500);
        }

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var signingCreds = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        // derive simple username claim (without domain part)
        var rawUsername = creds.Username ?? string.Empty;
        if (rawUsername.Contains("\\"))
            rawUsername = rawUsername.Split('\\', 2)[1];
        if (rawUsername.Contains("@"))
            rawUsername = rawUsername.Split('@', 2)[0];

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

        token.Payload.Add("sub", usernameForBind);
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
