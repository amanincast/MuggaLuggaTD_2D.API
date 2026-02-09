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
    public DbSet<GameInstance> GameInstances => Set<GameInstance>();
    public DbSet<WorldViewGameData> WorldViewGameData => Set<WorldViewGameData>();
    public DbSet<PlayerGameData> PlayerGameData => Set<PlayerGameData>();
    public DbSet<Alliance> Alliances => Set<Alliance>();
    public DbSet<MarketplaceListing> MarketplaceListings => Set<MarketplaceListing>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();

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

        // Configure GameInstance entity
        builder.Entity<GameInstance>(entity =>
        {
            entity.HasIndex(e => e.OwnerId);

            entity.HasOne(e => e.Owner)
                .WithMany(u => u.OwnedGameInstances)
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure WorldViewGameData entity (1:1 with GameInstance)
        builder.Entity<WorldViewGameData>(entity =>
        {
            entity.HasIndex(e => e.GameInstanceId).IsUnique();

            entity.HasOne(e => e.GameInstance)
                .WithOne(g => g.WorldViewGameData)
                .HasForeignKey<WorldViewGameData>(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure PlayerGameData entity
        builder.Entity<PlayerGameData>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId }).IsUnique();

            entity.HasOne(e => e.GameInstance)
                .WithMany(g => g.PlayerGameData)
                .HasForeignKey(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany(u => u.PlayerGameData)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure Alliance entity
        builder.Entity<Alliance>(entity =>
        {
            entity.HasIndex(e => e.GameInstanceId);
            entity.HasIndex(e => new { e.GameInstanceId, e.Name }).IsUnique();

            entity.HasOne(e => e.GameInstance)
                .WithMany(g => g.Alliances)
                .HasForeignKey(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure MarketplaceListing entity
        builder.Entity<MarketplaceListing>(entity =>
        {
            entity.HasIndex(e => e.GameInstanceId);
            entity.HasIndex(e => e.SellerId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.GameInstanceId, e.Status });

            entity.HasOne(e => e.GameInstance)
                .WithMany(g => g.MarketplaceListings)
                .HasForeignKey(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Seller)
                .WithMany(u => u.MarketplaceListingsAsSeller)
                .HasForeignKey(e => e.SellerId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Buyer)
                .WithMany(u => u.MarketplaceListingsAsBuyer)
                .HasForeignKey(e => e.BuyerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Configure Friendship entity
        builder.Entity<Friendship>(entity =>
        {
            entity.HasIndex(e => new { e.RequesterId, e.AddresseeId }).IsUnique();
            entity.HasIndex(e => e.RequesterId);
            entity.HasIndex(e => e.AddresseeId);
            entity.HasIndex(e => e.Status);

            entity.HasOne(e => e.Requester)
                .WithMany(u => u.SentFriendRequests)
                .HasForeignKey(e => e.RequesterId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Addressee)
                .WithMany(u => u.ReceivedFriendRequests)
                .HasForeignKey(e => e.AddresseeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Configure UserBlock entity
        builder.Entity<UserBlock>(entity =>
        {
            entity.HasIndex(e => new { e.BlockerId, e.BlockedUserId }).IsUnique();
            entity.HasIndex(e => e.BlockerId);
            entity.HasIndex(e => e.BlockedUserId);

            entity.HasOne(e => e.Blocker)
                .WithMany(u => u.BlockedUsers)
                .HasForeignKey(e => e.BlockerId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.BlockedUser)
                .WithMany(u => u.BlockedByUsers)
                .HasForeignKey(e => e.BlockedUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
