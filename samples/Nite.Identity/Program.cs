using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Nite;
using Nite.Identity.Data;
using Nite.Identity.Security;

// Sign-in for two single-page apps, kept on the server ("backend for frontend"): the browser holds
// only an HttpOnly session cookie, which no script can read, and the app calls its API on the same
// origin with that cookie plus a form token. No credential ever reaches JavaScript.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("App") ?? "Data Source=app.db"));

builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
    {
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services.ConfigureApplicationCookie(options =>
{
    // __Host-: only over HTTPS, only for this host, for the whole site; no script can read it, and
    // it is not sent with requests that other sites start.
    options.Cookie.Name = "__Host-session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    // Signed out after 30 idle minutes.
    options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    options.SlidingExpiration = true;
    options.LoginPath = "/login";
    // API calls get a status code, not a redirect to a page of HTML.
    options.Events.OnRedirectToLogin = context => StatusOrRedirect(context, StatusCodes.Status401Unauthorized);
    options.Events.OnRedirectToAccessDenied = context => StatusOrRedirect(context, StatusCodes.Status403Forbidden);
});

// Sessions live on the server, so signing out ends them, copies of the cookie included.
builder.Services.AddDistributedMemoryCache();
builder.Services.AddServerSideSessions();

// A changed password or role reaches signed-in sessions within a minute.
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = Csrf.HeaderName;
    options.Cookie.Name = "__Host-csrf";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    // SecurityHeaders sends X-Frame-Options: DENY on every response.
    options.SuppressXFrameOptionsHeader = true;
});

builder.Services.AddAuthorization(options =>
{
    // Everything needs a signed-in user unless it says otherwise: only /login and signing in do.
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy("Admin", policy => policy.RequireRole(Seed.AdminRole));
});

// Ten sign-in attempts a minute per address, on top of Identity's lockout per account.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("sign-in", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddProblemDetails();
builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

builder.Services.AddNite(options =>
{
    // Nite puts the request's nonce on what it writes inline, so the strict policy allows it.
    options.CspNonce = SecurityHeaders.Nonce;

    // What each page needs to start, written into it as it is served: a form token for the
    // requests that change data and, once signed in, who the user is. Not a credential: the
    // session stays in the HttpOnly cookie.
    options.ClientConfig = context =>
    {
        var csrfToken = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context).RequestToken;
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return new { CsrfToken = csrfToken };
        }
        return new
        {
            CsrfToken = csrfToken,
            User = new
            {
                Name = context.User.Identity.Name,
                Roles = context.User.FindAll(ClaimTypes.Role).Select(c => c.Value),
                // The security stamp is Identity's own bookkeeping; the browser has no use for it.
                Claims = context.User.Claims
                    .Where(c => c.Type != new ClaimsIdentityOptions().SecurityStampClaimType)
                    .Select(c => new { c.Type, c.Value }),
            },
        };
    };
});

var app = builder.Build();

// Creates app.db and the demo users on first run.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}
await app.RefuseDemoPasswordsOutsideDevelopmentAsync();

if (!app.Environment.IsDevelopment())
{
    // Errors as problem details, without stack traces; and HTTPS only, remembered by browsers.
    app.UseExceptionHandler();
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseSecurityHeaders();

// Development: compiles Client/ on request with hot updates. Production: caches hashed bundles for good.
app.UseNite();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

var account = app.MapGroup("/account").AddEndpointFilter(Csrf.Validate);

account.MapPost("/login", async (LoginRequest request, UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn, IPasswordHasher<IdentityUser> hasher) =>
{
    var user = await users.FindByNameAsync(request.UserName);
    if (user is null)
    {
        // Spend as long as checking a real password, so response times do not reveal which user
        // names exist.
        hasher.VerifyHashedPassword(UnknownUser.Instance, UnknownUser.PasswordHash, request.Password);
        return SignInFailed();
    }
    // One answer for a wrong password and a locked account, for the same reason.
    var result = await signIn.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);
    return result.Succeeded ? Results.NoContent() : SignInFailed();
}).AllowAnonymous().RequireRateLimiting("sign-in");

account.MapPost("/logout", async (SignInManager<IdentityUser> signIn) =>
{
    // Removes the server-side session, so the cookie is worthless even if it was copied.
    await signIn.SignOutAsync();
    return Results.NoContent();
});

// The API: same origin, the session cookie, and the form token on anything that changes data.
var api = app.MapGroup("/api").AddEndpointFilter(Csrf.Validate);

api.MapGet("/notes", async (ClaimsPrincipal user, AppDbContext db) =>
{
    var userId = UserId(user);
    // Newest first. By id, because SQLite cannot sort DateTimeOffset columns; ids follow creation order.
    return await db.Notes.Where(n => n.OwnerId == userId).OrderByDescending(n => n.Id)
        .Select(n => new NoteDto(n.Id, n.Text, n.Created)).ToListAsync();
});

api.MapPost("/notes", async (NewNote input, ClaimsPrincipal user, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > 500)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["Write between 1 and 500 characters."] });
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
    .RequireAuthorization("Admin");

// Built client files are public: the login page needs its own, and they hold no data.
app.MapStaticAssets().AllowAnonymous();

// Two single-page apps: the login page for everyone, the app itself for signed-in users (the
// fallback policy sends everyone else to /login).
app.MapNiteSpa("login/index.html", path: "/login").AllowAnonymous();
app.MapNiteSpa();

app.Run();

static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;

static IResult SignInFailed() =>
    Results.Problem("Wrong user name or password, or too many attempts. Try again in a few minutes.", statusCode: StatusCodes.Status401Unauthorized);

static Task StatusOrRedirect(Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> context, int status)
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

record LoginRequest(string UserName, string Password);
record NewNote(string Text);
record NoteDto(int Id, string Text, DateTimeOffset Created);

/// <summary>A stand-in to check passwords against when the user name does not exist.</summary>
static class UnknownUser
{
    public static readonly IdentityUser Instance = new();
    public static readonly string PasswordHash =
        new PasswordHasher<IdentityUser>().HashPassword(Instance, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
}
