using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nite;
using Nite.Identity.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("App") ?? "Data Source=app.db"));

builder.Services.AddIdentity<IdentityUser, IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>();

// Pages are protected by Identity's cookie: a browser navigating to a page cannot attach a token.
// Signed-out visitors go to the login SPA, and API calls get a status code instead of a redirect.
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.Events.OnRedirectToLogin = context => StatusOrRedirect(context, StatusCodes.Status401Unauthorized);
    options.Events.OnRedirectToAccessDenied = context => StatusOrRedirect(context, StatusCodes.Status403Forbidden);
});

// The API takes bearer tokens, which the main SPA receives with its page (ClientConfig below).
builder.Services.AddAuthentication()
    .AddBearerToken(IdentityConstants.BearerScheme, options => options.BearerTokenExpiration = TimeSpan.FromHours(1));

builder.Services.AddAuthorization(options =>
{
    // Everything needs a signed-in user unless it says otherwise: only /login and the sign-in call do.
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy("Api", policy => policy.AddAuthenticationSchemes(IdentityConstants.BearerScheme).RequireAuthenticatedUser());
});

builder.Services.AddNite(options => options.ClientConfig = context =>
{
    // The login page is served to everyone; only signed-in users get claims and a token.
    if (context.User.Identity?.IsAuthenticated != true)
    {
        return null;
    }

    // A bearer token for this user, made the way the bearer token handler reads it back.
    var bearer = context.RequestServices.GetRequiredService<IOptionsMonitor<BearerTokenOptions>>().Get(IdentityConstants.BearerScheme);
    var expires = DateTimeOffset.UtcNow + bearer.BearerTokenExpiration;
    var ticket = new AuthenticationTicket(context.User, new AuthenticationProperties { ExpiresUtc = expires }, IdentityConstants.BearerScheme);

    return new
    {
        User = new
        {
            Name = context.User.Identity.Name,
            Roles = context.User.FindAll(ClaimTypes.Role).Select(c => c.Value),
            // The security stamp is Identity's own bookkeeping; the browser has no use for it.
            Claims = context.User.Claims
                .Where(c => c.Type != new ClaimsIdentityOptions().SecurityStampClaimType)
                .Select(c => new { c.Type, c.Value }),
        },
        Token = bearer.BearerTokenProtector.Protect(ticket),
        TokenExpires = expires,
    };
});

var app = builder.Build();

// Creates app.db and the seeded users on first run.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

// Development: compiles Client/ on request with hot updates. Production: no-op.
app.UseNite();

app.UseAuthentication();
app.UseAuthorization();

// Sign-in and sign-out set and clear the cookie.
app.MapPost("/account/login", async (LoginRequest request, SignInManager<IdentityUser> signIn) =>
{
    var result = await signIn.PasswordSignInAsync(request.UserName, request.Password, request.Remember, lockoutOnFailure: true);
    return result.Succeeded ? Results.NoContent()
        : result.IsLockedOut ? Results.Problem("Too many failed attempts. Try again in a few minutes.", statusCode: StatusCodes.Status429TooManyRequests)
        : Results.Problem("Wrong user name or password.", statusCode: StatusCodes.Status401Unauthorized);
}).AllowAnonymous();

app.MapPost("/account/logout", async (SignInManager<IdentityUser> signIn) =>
{
    await signIn.SignOutAsync();
    return Results.NoContent();
});

// The API, for bearer tokens only.
var api = app.MapGroup("/api").RequireAuthorization("Api");

api.MapGet("/me", (ClaimsPrincipal user) => new
{
    Name = user.Identity?.Name,
    Claims = user.Claims.Select(c => new { c.Type, c.Value }),
});

api.MapGet("/notes", async (ClaimsPrincipal user, AppDbContext db) =>
{
    var userId = UserId(user);
    // Newest first. By id, because SQLite cannot sort DateTimeOffset columns; ids follow creation order.
    return await db.Notes.Where(n => n.OwnerId == userId).OrderByDescending(n => n.Id)
        .Select(n => new NoteDto(n.Id, n.Text, n.Created)).ToListAsync();
});

api.MapPost("/notes", async (NewNote input, ClaimsPrincipal user, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(input.Text))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["Write something first."] });
    }
    var note = new Note { OwnerId = UserId(user), Text = input.Text.Trim(), Created = DateTimeOffset.UtcNow };
    db.Notes.Add(note);
    await db.SaveChangesAsync();
    return Results.Created($"/api/notes/{note.Id}", new NoteDto(note.Id, note.Text, note.Created));
});

api.MapDelete("/notes/{id:int}", async (int id, ClaimsPrincipal user, AppDbContext db) =>
{
    var userId = UserId(user);
    return await db.Notes.Where(n => n.Id == id && n.OwnerId == userId).ExecuteDeleteAsync() == 1
        ? Results.NoContent()
        : Results.NotFound();
});

// Admins only: every user and how many notes they have.
api.MapGet("/users", async (AppDbContext db) =>
    await db.Users.OrderBy(u => u.UserName)
        .Select(u => new { u.UserName, u.Email, Notes = db.Notes.Count(n => n.OwnerId == u.Id) })
        .ToListAsync())
    .RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.BearerScheme).RequireRole(Seed.AdminRole));

// Built client files are public: the login page needs its own, and they hold no data.
app.MapStaticAssets().AllowAnonymous();

// Two single-page apps: the login page for everyone, the app itself for signed-in users (the
// fallback policy sends everyone else to /login).
app.MapNiteSpa("login/index.html", path: "/login").AllowAnonymous();
app.MapNiteSpa();

app.Run();

static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;

static Task StatusOrRedirect(RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> context, int status)
{
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/account"))
    {
        context.Response.StatusCode = status;
    }
    else
    {
        context.Response.Redirect(context.RedirectUri);
    }
    return Task.CompletedTask;
}

record LoginRequest(string UserName, string Password, bool Remember = false);
record NewNote(string Text);
record NoteDto(int Id, string Text, DateTimeOffset Created);
