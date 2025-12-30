using System.Net;
using System.DirectoryServices.Protocols;

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
// Accepts JSON body: { "username": "...", "password": "..." }
// Returns: { "authenticated": true/false }
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

        // Optionally allow an explicit domain from config to build username as DOMAIN\user
        var domain = config["DomainController:Domain"];
        var usernameForBind = creds.Username ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(domain) && !usernameForBind.Contains("\\") && !usernameForBind.Contains("@"))
        {
            usernameForBind = domain + "\\" + usernameForBind;
        }

        var credential = new NetworkCredential(usernameForBind, creds.Password ?? string.Empty);

        // Perform bind on a background thread (Bind is synchronous)
        await Task.Run(() => connection.Bind(credential));

        // If bind succeeded, authenticated
        return Results.Ok(new { authenticated = true });
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


internal record Credentials(string Username, string Password);
