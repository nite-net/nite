using Microsoft.EntityFrameworkCore;
using Nite.Identity.Data;

namespace Nite.Identity.Security;

/// <summary>
/// The demo users' passwords are public, in this repository. Outside Development the app refuses
/// to start while any user still has one of them: change those passwords or delete the users first.
/// </summary>
public static class DemoUsers
{
    public static async Task RefuseDemoPasswordsOutsideDevelopmentAsync(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            return;
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var exposed = await db.Users
            .Where(u => u.PasswordHash != null && Seed.DemoPasswordHashes.Contains(u.PasswordHash))
            .Select(u => u.UserName)
            .ToListAsync();
        if (exposed.Count > 0)
        {
            throw new InvalidOperationException(
                $"The demo users {string.Join(" and ", exposed)} still have their published demo passwords. " +
                "This app refuses to run outside Development until those passwords are changed or the users deleted.");
        }
    }
}
