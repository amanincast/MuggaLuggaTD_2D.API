using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<GameSave> GameSaves => Set<GameSave>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Configure GameSave entity
        builder.Entity<GameSave>(entity =>
        {
            entity.HasIndex(e => new { e.UserId, e.SlotName }).IsUnique();

            entity.HasOne(e => e.User)
                .WithMany(u => u.GameSaves)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
