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
    public DbSet<PveRun> PveRuns => Set<PveRun>();
    public DbSet<RegionRaid> RegionRaids => Set<RegionRaid>();
    public DbSet<SeasonScore> SeasonScores => Set<SeasonScore>();
    public DbSet<SeasonResult> SeasonResults => Set<SeasonResult>();
    public DbSet<Siege> Sieges => Set<Siege>();
    public DbSet<WarLogEntry> WarLog => Set<WarLogEntry>();
    public DbSet<PlayerMaterial> PlayerMaterials => Set<PlayerMaterial>();
    public DbSet<ItemGrant> ItemGrants => Set<ItemGrant>();
    public DbSet<ItemLedgerState> ItemLedgerStates => Set<ItemLedgerState>();
    public DbSet<PlayerGold> PlayerGold => Set<PlayerGold>();
    public DbSet<TavernRecruit> TavernRecruits => Set<TavernRecruit>();
    public DbSet<TavernLure> TavernLures => Set<TavernLure>();
    public DbSet<TavernState> TavernStates => Set<TavernState>();
    public DbSet<FirstStepsProgress> FirstStepsProgress => Set<FirstStepsProgress>();
    public DbSet<HiringCandidate> HiringCandidates => Set<HiringCandidate>();
    public DbSet<HiredWorker> HiredWorkers => Set<HiredWorker>();
    public DbSet<HiringState> HiringStates => Set<HiringState>();
    public DbSet<RegionFortification> RegionFortifications => Set<RegionFortification>();
    public DbSet<HiredCharacter> HiredCharacters => Set<HiredCharacter>();
    public DbSet<PlayerParty> PlayerParties => Set<PlayerParty>();
    public DbSet<InviteCode> InviteCodes => Set<InviteCode>();
    public DbSet<PlayerSiteClear> PlayerSiteClears => Set<PlayerSiteClear>();
    public DbSet<BloodiedCharacter> BloodiedCharacters => Set<BloodiedCharacter>();
    public DbSet<AutoFightReport> AutoFightReports => Set<AutoFightReport>();
    public DbSet<FactionState> FactionStates => Set<FactionState>();
    public DbSet<FactionRaid> FactionRaids => Set<FactionRaid>();
    public DbSet<FactionSiege> FactionSieges => Set<FactionSiege>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Invite codes are looked up by the code a tester types.
        builder.Entity<InviteCode>(entity =>
        {
            entity.HasIndex(e => e.Code).IsUnique();
        });

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

        // Configure PveRun entity (a player's attempt at a PvE location)
        builder.Entity<PveRun>(entity =>
        {
            // Claims look up a user's open runs for an instance, so index that path.
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId, e.ClaimedAt });

            entity.HasOne(e => e.GameInstance)
                .WithMany()
                .HasForeignKey(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure RegionRaid entity (one player's raid on one rival region)
        builder.Entity<RegionRaid>(entity =>
        {
            // Every raid looks up this attacker's last raid on this region to check the cooldown,
            // so that is the path to index — newest first, since only the last one matters.
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId, e.RegionId, e.RaidedAt });

            // And a defender reads the log for their own region.
            entity.HasIndex(e => new { e.GameInstanceId, e.RegionId, e.RaidedAt });

            entity.HasOne(e => e.GameInstance)
                .WithMany()
                .HasForeignKey(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure PlayerSiteClear (one player's last clear of one site: SiteRotationRules)
        builder.Entity<PlayerSiteClear>(entity =>
        {
            // One row per player per site, read on every begin and claim.
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId, e.SiteId }).IsUnique();

            entity.HasOne(e => e.GameInstance)
                .WithMany()
                .HasForeignKey(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // A company in auto mode is settled by whichever request reads it first. Two at once (the Hall
        // reads the companies and the reports together) would each replay the same stretch and pay it
        // twice, so the settle's save is conditional on where the last one left off: the second fails
        // as a whole, and nothing it wrote survives (AutoFightService.SettleAsync).
        builder.Entity<PlayerParty>().Property(e => e.AutoSettledAt).IsConcurrencyToken();

        // Bloodied characters (BloodiedRules): one row per character, asked before every fight.
        // One row per faction per realm (docs/design/npc-factions.md).
        builder.Entity<FactionState>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.Faction }).IsUnique();

            entity.HasOne(e => e.GameInstance)
                .WithMany()
                .HasForeignKey(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // A faction's raids, found by realm and time for the cooldown.
        builder.Entity<FactionRaid>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.RaidedAt });

            entity.HasOne(e => e.GameInstance)
                .WithMany()
                .HasForeignKey(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // A faction's sieges, found by realm and state (live ones) and by when their muster closes.
        builder.Entity<FactionSiege>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.State });
            entity.HasIndex(e => new { e.State, e.MusterEndsAt });

            entity.HasOne(e => e.GameInstance)
                .WithMany()
                .HasForeignKey(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<BloodiedCharacter>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId, e.CharacterId }).IsUnique();

            entity.HasOne(e => e.GameInstance)
                .WithMany()
                .HasForeignKey(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Auto-fight reports, banked until the client collects their experience and gear.
        builder.Entity<AutoFightReport>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId, e.CollectedAt });

            entity.HasOne(e => e.GameInstance)
                .WithMany()
                .HasForeignKey(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure SeasonScore entity (one player's running score in one season)
        builder.Entity<SeasonScore>(entity =>
        {
            // A player has exactly one score per season of an instance; the unique index is what
            // makes settle-up safe to call from every path that can change a holding.
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId, e.SeasonNumber }).IsUnique();

            entity.HasOne(e => e.GameInstance)
                .WithMany()
                .HasForeignKey(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure SeasonResult entity (where a player finished a closed season)
        builder.Entity<SeasonResult>(entity =>
        {
            // Written once per player per season, and read back as a table.
            entity.HasIndex(e => new { e.GameInstanceId, e.SeasonNumber, e.Rank });

            // And read across instances, for what a player has won anywhere — the carry-over's path.
            entity.HasIndex(e => new { e.UserId, e.SeasonEndedAt });

            entity.HasOne(e => e.GameInstance)
                .WithMany()
                .HasForeignKey(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure PlayerMaterial (one row per player per material per realm; the wallet reads and
        // writes by that key, so it is unique)
        builder.Entity<PlayerMaterial>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId, e.MaterialName }).IsUnique();
        });

        // The item ledger: an item's id is unique in its realm (it is how a save names it), and it is
        // read by holder and by listing.
        builder.Entity<ItemGrant>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.ItemId }).IsUnique();
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId });
            entity.HasIndex(e => e.ListingId);
        });

        // One adoption per player per realm.
        builder.Entity<ItemLedgerState>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId }).IsUnique();
        });

        // Configure PlayerGold (one purse per player per realm; every read and write is by that
        // key, and a second row would be a second balance quietly accruing alongside the first)
        builder.Entity<PlayerGold>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId }).IsUnique();
        });

        // Configure PlayerParty (a player's companies in a realm, always read as a set by that key)
        builder.Entity<PlayerParty>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId });
        });

        // Configure TavernState (one row per player per realm; it is read and written on every
        // refresh and every clear, and a second row would be a second refresh count)
        builder.Entity<TavernState>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId }).IsUnique();
        });

        // The Hiring Hall: a board of seats, the workers, and one state row per player per realm.
        builder.Entity<HiringCandidate>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId, e.Slot }).IsUnique();
        });
        builder.Entity<HiredWorker>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId });
            entity.HasIndex(e => new { e.GameInstanceId, e.SiteId });
        });
        builder.Entity<HiringState>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId }).IsUnique();
        });

        // Configure RegionFortification (the sweep looks for works under way that are due)
        builder.Entity<RegionFortification>(entity =>
        {
            entity.HasIndex(e => new { e.State, e.CompletesAt });
            entity.HasIndex(e => new { e.GameInstanceId, e.RegionId });
        });

        // Configure FirstStepsProgress (one row per player per realm: a second row would be a second chest)
        builder.Entity<FirstStepsProgress>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId }).IsUnique();
        });

        // Configure TavernLure (one row per player per affinity: the offer standing on it, and the
        // pity owed on it. A second row for the same affinity would be a second, divergent debt)
        builder.Entity<TavernLure>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId, e.Affinity }).IsUnique();
        });

        // Configure TavernRecruit (one player's board in one realm; read and replaced by that key)
        builder.Entity<TavernRecruit>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId, e.Slot }).IsUnique();
        });

        // Configure HiredCharacter (the entitlement record every save is reconciled against, looked
        // up by the character id the save carries)
        builder.Entity<HiredCharacter>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.UserId, e.CharacterId }).IsUnique();
        });

        // Configure WarLogEntry entity (a realm's war log, read newest first per season)
        builder.Entity<WarLogEntry>(entity =>
        {
            entity.HasIndex(e => new { e.GameInstanceId, e.SeasonNumber, e.OccurredAt });
        });

        // Configure Siege entity (one player's siege of one rival region)
        builder.Entity<Siege>(entity =>
        {
            // The scheduler looks for live sieges whose windows have closed, across every realm.
            entity.HasIndex(e => new { e.State, e.MusterEndsAt });
            entity.HasIndex(e => new { e.State, e.AssaultEndsAt });

            // Declaring checks this region for a live siege, and this attacker for one anywhere.
            entity.HasIndex(e => new { e.GameInstanceId, e.RegionId, e.State });
            entity.HasIndex(e => new { e.GameInstanceId, e.AttackerUserId, e.State });

            entity.HasOne(e => e.GameInstance)
                .WithMany()
                .HasForeignKey(e => e.GameInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Attacker)
                .WithMany()
                .HasForeignKey(e => e.AttackerUserId)
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
            // A material queue is read by kind, status and name, oldest first.
            entity.HasIndex(e => new { e.Kind, e.Status, e.GoodsKey, e.CreatedAt });

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
