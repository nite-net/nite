using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Nite.Identity.Data;

/// <summary>
/// The demo's users, created by the initial migration: admin/admin (in the Admin role) and
/// benson/hedges. Everything is a constant, because migrations compare the model each time: a
/// password hashed here at run time would get a new salt, and EF would see a changed model on every
/// build. The hashes came from <see cref="PasswordHasher{TUser}"/>. Seeding skips Identity's
/// password rules, which would reject passwords this short for anyone signing up. Because these
/// passwords are public, the app will not start outside Development while they are still in use
/// (<see cref="Security.DemoUsers"/>).
/// </summary>
internal static class Seed
{
    public const string AdminRole = "Admin";

    private const string AdminPasswordHash = "AQAAAAIAAYagAAAAEODiDX1efDhq8qqnrD2pD0WI0E5xXrlroamxMoU9SKnKbsrnKa6gilt4bT8zABkfLg==";
    private const string BensonPasswordHash = "AQAAAAIAAYagAAAAEO901dzVMhVZPtSbDR90Ni8PSYIq62FWmkmNgZHIRySSLT1ljfmPm0KDHTFr5v3FzQ==";

    /// <summary>The seeded password hashes, whose passwords are public; see <see cref="Security.DemoUsers"/>.</summary>
    public static readonly string[] DemoPasswordHashes = [AdminPasswordHash, BensonPasswordHash];

    private const string AdminRoleId = "4f3c1c7e-2a7b-4f7e-9d0a-6a1d8a6e0b01";
    private const string AdminId = "0b8f9a52-5c1e-4c55-8f0e-1d2a3b4c5d01";
    private const string BensonId = "0b8f9a52-5c1e-4c55-8f0e-1d2a3b4c5d02";

    public static void Apply(ModelBuilder builder)
    {
        builder.Entity<IdentityRole>().HasData(new IdentityRole
        {
            Id = AdminRoleId,
            Name = AdminRole,
            NormalizedName = AdminRole.ToUpperInvariant(),
            ConcurrencyStamp = "7c0e3f7a-1b2d-4e5f-8a9b-0c1d2e3f4a01",
        });

        builder.Entity<IdentityUser>().HasData(
            User(AdminId, "admin", "admin@example.com", AdminPasswordHash, stamp: "A1"),
            User(BensonId, "benson", "benson@example.com", BensonPasswordHash, stamp: "B2"));

        builder.Entity<IdentityUserRole<string>>().HasData(new IdentityUserRole<string> { UserId = AdminId, RoleId = AdminRoleId });

        // A claim of their own each, to show claims reaching the browser.
        builder.Entity<IdentityUserClaim<string>>().HasData(
            new IdentityUserClaim<string> { Id = 1, UserId = AdminId, ClaimType = "department", ClaimValue = "Operations" },
            new IdentityUserClaim<string> { Id = 2, UserId = BensonId, ClaimType = "department", ClaimValue = "Sales" },
            new IdentityUserClaim<string> { Id = 3, UserId = BensonId, ClaimType = ClaimTypes.GivenName, ClaimValue = "Benson" });
    }

    private static IdentityUser User(string id, string name, string email, string passwordHash, string stamp) => new()
    {
        Id = id,
        UserName = name,
        NormalizedUserName = name.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        EmailConfirmed = true,
        PasswordHash = passwordHash,
        // Fixed, so the migration is stable; Identity replaces them when a password changes.
        SecurityStamp = $"SEEDSTAMP{stamp}000000000000000000000",
        ConcurrencyStamp = $"0b8f9a52-0000-4000-8000-0000000000{stamp}",
        LockoutEnabled = true,
    };
}
