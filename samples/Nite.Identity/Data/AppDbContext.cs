using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Nite.Identity.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Note>(note =>
        {
            note.Property(n => n.Text).HasMaxLength(500);
            note.HasIndex(n => n.OwnerId);
            note.HasOne<IdentityUser>().WithMany().HasForeignKey(n => n.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });

        Seed.Apply(builder);
    }
}

public sealed class Note
{
    public int Id { get; set; }
    public required string OwnerId { get; set; }
    public required string Text { get; set; }
    public DateTimeOffset Created { get; set; }
}
