namespace Nullify.CalDav.Data;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }

    /// <summary>ASP.NET Identity compatible password hash. Null for SSO-only accounts.</summary>
    public string? PasswordHash { get; set; }

    public bool IsAdmin { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Stable external identity ("issuer|sub") when the account is linked to OpenID Connect.</summary>
    public string? ExternalSubject { get; set; }

    /// <summary>Changes whenever credentials or roles change; invalidates existing sessions.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }

    /// <summary>Where the account password is verified.</summary>
    public AuthSource AuthSource { get; set; } = AuthSource.Local;

    /// <summary>Login name sent to the IMAP server; defaults to the email address, then the username.</summary>
    public string? ImapLogin { get; set; }

    /// <summary>Per-account IMAP server override; null uses the server-wide mail settings.</summary>
    public string? ImapHost { get; set; }
    public int? ImapPort { get; set; }
    public MailSecurity? ImapSecurity { get; set; }

    /// <summary>Storage quota in bytes; null uses the server default, 0 means unlimited.</summary>
    public long? QuotaBytes { get; set; }

    public List<Calendar> Calendars { get; set; } = [];
    public List<AddressBook> AddressBooks { get; set; } = [];
    public List<AppPassword> AppPasswords { get; set; } = [];
}

/// <summary>Per-device password used by CalDAV/CardDAV clients.</summary>
public class AppPassword
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the generated token (tokens are high entropy, so a fast hash is sufficient).</summary>
    public string TokenHash { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedUtc { get; set; }
}

public class Calendar
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    /// <summary>Path segment within the owner's calendar home.</summary>
    public string Uri { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public string? Color { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Comma separated list of supported components (VEVENT, VTODO, VJOURNAL).</summary>
    public string Components { get; set; } = "VEVENT,VTODO,VJOURNAL";

    /// <summary>VTIMEZONE text (CALDAV:calendar-timezone).</summary>
    public string? TimeZone { get; set; }

    public long SyncToken { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    public CalendarKind Kind { get; set; } = CalendarKind.Normal;

    /// <summary>Source URL of a subscribed (read-only) calendar.</summary>
    public string? SubscriptionUrl { get; set; }
    public int SubscriptionRefreshMinutes { get; set; } = 360;
    public DateTime? SubscriptionLastFetchUtc { get; set; }
    public string? SubscriptionETag { get; set; }
    public string? SubscriptionError { get; set; }

    /// <summary>Secret token of the public read-only iCalendar feed; null when not published.</summary>
    public string? PublicToken { get; set; }

    /// <summary>Publish only busy times (no titles, locations or descriptions).</summary>
    public bool PublicBusyOnly { get; set; }

    public bool IsReadOnlyKind => Kind is CalendarKind.Subscription or CalendarKind.Birthdays;

    public List<CalendarObject> Objects { get; set; } = [];
    public List<CalendarShare> Shares { get; set; } = [];

    public IReadOnlyList<string> ComponentList =>
        Components.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public class CalendarObject
{
    public int Id { get; set; }
    public int CalendarId { get; set; }
    public Calendar Calendar { get; set; } = null!;
    public string Uri { get; set; } = "";
    public string Uid { get; set; } = "";

    /// <summary>VEVENT, VTODO or VJOURNAL.</summary>
    public string ComponentType { get; set; } = "";
    public string Data { get; set; } = "";
    public string ETag { get; set; } = "";
    public int Size { get; set; }

    /// <summary>Unix seconds of the first instance start, or null when unbounded.</summary>
    public long? FirstOccurrence { get; set; }

    /// <summary>Unix seconds of the last instance end, or null when unbounded.</summary>
    public long? LastOccurrence { get; set; }

    /// <summary>RFC 6638 Schedule-Tag; changes only on scheduling-significant updates.</summary>
    public string? ScheduleTag { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
}

public class AddressBook
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    public string Uri { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public long SyncToken { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    public AddressBookKind Kind { get; set; } = AddressBookKind.Normal;

    public List<Card> Cards { get; set; } = [];
    public List<AddressBookShare> Shares { get; set; } = [];
}

public class Card
{
    public int Id { get; set; }
    public int AddressBookId { get; set; }
    public AddressBook AddressBook { get; set; } = null!;
    public string Uri { get; set; } = "";
    public string Uid { get; set; } = "";
    public string? FormattedName { get; set; }
    public string Data { get; set; } = "";
    public string ETag { get; set; } = "";
    public int Size { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
}

public enum ChangeOperation : byte
{
    Added = 1,
    Modified = 2,
    Deleted = 3,
}

public class CalendarChange
{
    public long Id { get; set; }
    public int CalendarId { get; set; }
    public Calendar Calendar { get; set; } = null!;
    public string Uri { get; set; } = "";
    public long SyncToken { get; set; }
    public ChangeOperation Operation { get; set; }
}

public class CardChange
{
    public long Id { get; set; }
    public int AddressBookId { get; set; }
    public AddressBook AddressBook { get; set; } = null!;
    public string Uri { get; set; } = "";
    public long SyncToken { get; set; }
    public ChangeOperation Operation { get; set; }
}

public enum ShareAccess : byte
{
    Read = 1,
    ReadWrite = 2,
}

public class CalendarShare
{
    public int Id { get; set; }
    public int CalendarId { get; set; }
    public Calendar Calendar { get; set; } = null!;

    /// <summary>The user the calendar is shared with (null for group shares).</summary>
    public int? ShareeId { get; set; }
    public User? Sharee { get; set; }

    /// <summary>The group the calendar is shared with (null for user shares).</summary>
    public int? GroupId { get; set; }
    public Group? Group { get; set; }
    public ShareAccess Access { get; set; } = ShareAccess.Read;

    /// <summary>Path segment within the sharee's calendar home.</summary>
    public string Uri { get; set; } = "";

    /// <summary>Sharee-specific overrides (set via PROPPATCH from the sharee's client).</summary>
    public string? DisplayName { get; set; }
    public string? Color { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class AddressBookShare
{
    public int Id { get; set; }
    public int AddressBookId { get; set; }
    public AddressBook AddressBook { get; set; } = null!;
    public int? ShareeId { get; set; }
    public User? Sharee { get; set; }
    public int? GroupId { get; set; }
    public Group? Group { get; set; }
    public ShareAccess Access { get; set; } = ShareAccess.Read;
    public string Uri { get; set; } = "";
    public string? DisplayName { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class Setting
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}

public enum AuthSource : byte
{
    Local = 0,
    Imap = 1,
}

public enum MailSecurity : byte
{
    /// <summary>Implicit TLS (IMAPS on 993, SMTPS on 465).</summary>
    Tls = 0,
    StartTls = 1,
    None = 2,
}

/// <summary>A SyncML client device that has synchronised with the server.</summary>
public class SyncMlDevice
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Device identifier (SyncHdr/Source/LocURI, e.g. "IMEI:..." ).</summary>
    public string DeviceId { get; set; } = "";
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SoftwareVersion { get; set; }

    /// <summary>Preferred content types per client datastore, as "datastore=type" lines.</summary>
    public string? PreferredTypes { get; set; }

    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSyncUtc { get; set; } = DateTime.UtcNow;

    public List<SyncMlAnchor> Anchors { get; set; } = [];
}

/// <summary>Synchronisation state of one client datastore against one server collection.</summary>
public class SyncMlAnchor
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public SyncMlDevice Device { get; set; } = null!;

    /// <summary>Client datastore URI (e.g. "./contacts").</summary>
    public string ClientDatabase { get; set; } = "";

    /// <summary>Server database URI requested by the client (e.g. "contacts" or "calendar/work").</summary>
    public string ServerDatabase { get; set; } = "";

    public string? LastClientAnchor { get; set; }
    public string? LastServerAnchor { get; set; }

    /// <summary>Collection sync token at the end of the last successful session.</summary>
    public long ServerSyncToken { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public List<SyncMlMapping> Mappings { get; set; } = [];
}

/// <summary>Maps a client local id (LUID) to a server resource name.</summary>
public class SyncMlMapping
{
    public long Id { get; set; }
    public int AnchorId { get; set; }
    public SyncMlAnchor Anchor { get; set; } = null!;
    public string Luid { get; set; } = "";
    public string ServerUri { get; set; } = "";
}

public enum CalendarKind : byte
{
    Normal = 0,
    Subscription = 1,
    Birthdays = 2,
    /// <summary>RFC 6638 scheduling inbox.</summary>
    Inbox = 3,
}

public enum AddressBookKind : byte
{
    Normal = 0,
    /// <summary>Read-only global address book of all users.</summary>
    Directory = 1,
}

public class Group
{
    public int Id { get; set; }

    /// <summary>URL-safe name used in the group principal URL.</summary>
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<GroupMember> Members { get; set; } = [];
}

public class GroupMember
{
    public int GroupId { get; set; }
    public Group Group { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;
}

/// <summary>Grants another user access to all of the owner's calendars (CalendarServer proxy).</summary>
public class Delegation
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    public int DelegateId { get; set; }
    public User Delegate { get; set; } = null!;
    public bool CanWrite { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum TrashKind : byte
{
    CalendarObject = 1,
    Card = 2,
    Calendar = 3,
    AddressBook = 4,
}

/// <summary>A deleted resource or collection kept for a retention period so it can be restored.</summary>
public class TrashItem
{
    public long Id { get; set; }
    public int OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    public TrashKind Kind { get; set; }

    /// <summary>Collection the item belonged to (resources only).</summary>
    public int? CollectionId { get; set; }
    public string CollectionName { get; set; } = "";

    /// <summary>Human readable summary (event title, contact name or collection name).</summary>
    public string Title { get; set; } = "";
    public string Uri { get; set; } = "";

    /// <summary>Resource data, or JSON describing a collection and its contents.</summary>
    public string Payload { get; set; } = "";
    public long Size { get; set; }
    public DateTime DeletedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A WebDAV-Push (Web Push) subscription for a collection.</summary>
public class PushRegistration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>"calendar" or "addressbook".</summary>
    public string CollectionType { get; set; } = "";
    public int CollectionId { get; set; }
    public string PushResource { get; set; } = "";
    public string? PublicKey { get; set; }
    public string? AuthSecret { get; set; }
    public bool ContentUpdate { get; set; } = true;
    public bool PropertyUpdate { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
public enum MigrationStatus : byte
{
    Ready = 0,
    Queued = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
}

/// <summary>A remote CalDAV/CardDAV account a user imports data from.</summary>
public class MigrationSource
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";

    /// <summary>Password protected with ASP.NET Core data protection (kept so imports can be re-run).</summary>
    public string? ProtectedPassword { get; set; }

    public MigrationStatus Status { get; set; } = MigrationStatus.Ready;
    public string? Message { get; set; }
    public int ItemsTotal { get; set; }
    public int ItemsDone { get; set; }
    public int ItemsFailed { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunUtc { get; set; }
    public List<MigrationCollection> Collections { get; set; } = [];
}

/// <summary>A collection discovered on the remote server and where it is imported locally.</summary>
public class MigrationCollection
{
    public int Id { get; set; }
    public int SourceId { get; set; }
    public MigrationSource Source { get; set; } = null!;

    /// <summary>"calendar" or "addressbook".</summary>
    public string Kind { get; set; } = "";
    public string RemoteUrl { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public string? Color { get; set; }
    public string? Components { get; set; }
    public bool Selected { get; set; } = true;

    /// <summary>The account could only read this collection on the old server.</summary>
    public bool ReadOnly { get; set; }
    public int? LocalCalendarId { get; set; }
    public int? LocalAddressBookId { get; set; }
    public int ItemsImported { get; set; }
    public string? LastError { get; set; }
}