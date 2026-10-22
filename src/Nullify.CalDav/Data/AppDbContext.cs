using Microsoft.EntityFrameworkCore;

namespace Nullify.CalDav.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, CollectionChangeNotifier? notifier = null) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<AppPassword> AppPasswords => Set<AppPassword>();
    public DbSet<Calendar> Calendars => Set<Calendar>();
    public DbSet<CalendarObject> CalendarObjects => Set<CalendarObject>();
    public DbSet<CalendarChange> CalendarChanges => Set<CalendarChange>();
    public DbSet<CalendarShare> CalendarShares => Set<CalendarShare>();
    public DbSet<AddressBook> AddressBooks => Set<AddressBook>();
    public DbSet<Card> Cards => Set<Card>();
    public DbSet<CardChange> CardChanges => Set<CardChange>();
    public DbSet<AddressBookShare> AddressBookShares => Set<AddressBookShare>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<SyncMlDevice> SyncMlDevices => Set<SyncMlDevice>();
    public DbSet<SyncMlAnchor> SyncMlAnchors => Set<SyncMlAnchor>();
    public DbSet<SyncMlMapping> SyncMlMappings => Set<SyncMlMapping>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<Delegation> Delegations => Set<Delegation>();
    public DbSet<TrashItem> TrashItems => Set<TrashItem>();
    public DbSet<PushRegistration> PushRegistrations => Set<PushRegistration>();
    public DbSet<MigrationSource> MigrationSources => Set<MigrationSource>();
    public DbSet<MigrationCollection> MigrationCollections => Set<MigrationCollection>();

    private static readonly HashSet<string> BookkeepingProperties =
        [nameof(Calendar.SyncToken), nameof(Calendar.ModifiedUtc), nameof(Calendar.SubscriptionLastFetchUtc), nameof(Calendar.SubscriptionETag), nameof(Calendar.SubscriptionError)];

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        List<CollectionChange>? changes = null;
        var usersChanged = false;
        if (notifier != null)
        {
            usersChanged = ChangeTracker.Entries<User>().Any(e => e.State is EntityState.Added or EntityState.Deleted ||
                (e.State == EntityState.Modified && e.Properties.Any(p => p.IsModified &&
                    p.Metadata.Name is nameof(User.DisplayName) or nameof(User.Email) or nameof(User.IsEnabled) or nameof(User.Username))));
            foreach (var entry in ChangeTracker.Entries())
            {
                CollectionChange? change = entry.Entity switch
                {
                    CalendarChange c when entry.State == EntityState.Added => new CollectionChange(CollectionChange.Calendar, c.CalendarId, true),
                    CardChange c when entry.State == EntityState.Added => new CollectionChange(CollectionChange.AddressBook, c.AddressBookId, true),
                    Calendar c when entry.State == EntityState.Modified && PropertiesChanged(entry) => new CollectionChange(CollectionChange.Calendar, c.Id, false),
                    AddressBook b when entry.State == EntityState.Modified && PropertiesChanged(entry) => new CollectionChange(CollectionChange.AddressBook, b.Id, false),
                    _ => null,
                };
                if (change != null) (changes ??= []).Add(change);
            }
        }
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (changes != null) notifier!.Publish(changes);
        if (usersChanged) notifier!.RaiseUsersChanged();
        return result;
    }

    private static bool PropertiesChanged(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry) =>
        entry.Properties.Any(p => p.IsModified && !BookkeepingProperties.Contains(p.Metadata.Name));

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.Property(x => x.Username).HasMaxLength(64).UseCollation("NOCASE");
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256).UseCollation("NOCASE");
            e.HasIndex(x => x.ExternalSubject).IsUnique();
        });

        b.Entity<AppPassword>(e =>
        {
            e.HasOne(x => x.User).WithMany(x => x.AppPasswords).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.TokenHash);
        });

        b.Entity<Calendar>(e =>
        {
            e.HasOne(x => x.Owner).WithMany(x => x.Calendars).HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OwnerId, x.Uri }).IsUnique();
        });

        b.Entity<CalendarObject>(e =>
        {
            e.HasOne(x => x.Calendar).WithMany(x => x.Objects).HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CalendarId, x.Uri }).IsUnique();
            e.HasIndex(x => new { x.CalendarId, x.Uid });
        });

        b.Entity<CalendarChange>(e =>
        {
            e.HasOne(x => x.Calendar).WithMany().HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CalendarId, x.SyncToken });
        });

        b.Entity<CalendarShare>(e =>
        {
            e.HasOne(x => x.Calendar).WithMany(x => x.Shares).HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Sharee).WithMany().HasForeignKey(x => x.ShareeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CalendarId, x.ShareeId }).IsUnique();
            e.HasIndex(x => new { x.CalendarId, x.GroupId }).IsUnique();
            e.HasIndex(x => new { x.ShareeId, x.Uri }).IsUnique();
        });

        b.Entity<AddressBook>(e =>
        {
            e.HasOne(x => x.Owner).WithMany(x => x.AddressBooks).HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OwnerId, x.Uri }).IsUnique();
        });

        b.Entity<Card>(e =>
        {
            e.HasOne(x => x.AddressBook).WithMany(x => x.Cards).HasForeignKey(x => x.AddressBookId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AddressBookId, x.Uri }).IsUnique();
            e.HasIndex(x => new { x.AddressBookId, x.Uid });
        });

        b.Entity<CardChange>(e =>
        {
            e.HasOne(x => x.AddressBook).WithMany().HasForeignKey(x => x.AddressBookId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AddressBookId, x.SyncToken });
        });

        b.Entity<AddressBookShare>(e =>
        {
            e.HasOne(x => x.AddressBook).WithMany(x => x.Shares).HasForeignKey(x => x.AddressBookId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Sharee).WithMany().HasForeignKey(x => x.ShareeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AddressBookId, x.ShareeId }).IsUnique();
            e.HasIndex(x => new { x.AddressBookId, x.GroupId }).IsUnique();
            e.HasIndex(x => new { x.ShareeId, x.Uri }).IsUnique();
        });

        b.Entity<Setting>(e => e.HasKey(x => x.Key));

        b.Entity<Calendar>().HasIndex(x => x.PublicToken).IsUnique();

        b.Entity<MigrationSource>(e =>
        {
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<MigrationCollection>(e =>
        {
            e.HasOne(x => x.Source).WithMany(x => x.Collections).HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SourceId, x.RemoteUrl }).IsUnique();
        });

        b.Entity<Group>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(64).UseCollation("NOCASE");
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<GroupMember>(e =>
        {
            e.HasKey(x => new { x.GroupId, x.UserId });
            e.HasOne(x => x.Group).WithMany(x => x.Members).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Delegation>(e =>
        {
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Delegate).WithMany().HasForeignKey(x => x.DelegateId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OwnerId, x.DelegateId }).IsUnique();
        });

        b.Entity<TrashItem>(e =>
        {
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OwnerId, x.DeletedUtc });
        });

        b.Entity<PushRegistration>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CollectionType, x.CollectionId });
            e.HasIndex(x => x.PushResource);
        });

        b.Entity<SyncMlDevice>(e =>
        {
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.DeviceId }).IsUnique();
        });

        b.Entity<SyncMlAnchor>(e =>
        {
            e.HasOne(x => x.Device).WithMany(x => x.Anchors).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.DeviceId, x.ClientDatabase, x.ServerDatabase }).IsUnique();
        });

        b.Entity<SyncMlMapping>(e =>
        {
            e.HasOne(x => x.Anchor).WithMany(x => x.Mappings).HasForeignKey(x => x.AnchorId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AnchorId, x.Luid }).IsUnique();
            e.HasIndex(x => new { x.AnchorId, x.ServerUri });
        });
    }
}
