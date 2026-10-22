# Nullify.CalDav

A self-hosted, multi-user **CalDAV, CardDAV and SyncML server** for calendars, tasks, notes and contacts — in the spirit of Baïkal — built on .NET 10 and SQLite and designed to run in Docker on Linux behind a reverse proxy such as Caddy.

- **Events** (`VEVENT`), **tasks** (`VTODO`) and **notes/journals** (`VJOURNAL`) over CalDAV
- **Contacts** (vCard 3.0/4.0) over CardDAV
- **SyncML** (OMA DS 1.1/1.2, XML and WBXML) for phones and apps that predate CalDAV, with vCard 2.1 / vCalendar 1.0 / vNote conversion
- **Multi-user** with **sharing** of calendars and address books with people or **groups** (read-only or read/write), and **calendar delegation**
- **Meeting invitations**: server-side scheduling (RFC 6638) with delivery to local users and **iMIP email** to everyone else
- **Instant sync** for DAVx⁵ via **WebDAV-Push** (Web Push / UnifiedPush)
- **Trash** with restore, **storage quotas**, **calendar subscriptions** (webcal), **public calendar links**
- Generated **birthday calendar** and a **global address book** of all users
- **Migration** from another CalDAV/CardDAV server (Baïkal, Nextcloud, Radicale, iCloud, …): calendars, tasks, notes and contacts
- **vCard 3.0/4.0 negotiation**: each client gets the version it asks for
- **Web management UI** with first-run admin setup, user administration, import/export
- **Authentication**: local passwords, **per-account IMAP passthrough**, per-device **app passwords**, optional **OpenID Connect** for the UI
- **Client autoconfiguration**: Thunderbird autoconfig (incl. CalDAV/CardDAV), Outlook Autodiscover, Apple configuration profiles, `/.well-known` discovery
- Single SQLite file for storage; only Microsoft packages are used

## Quick start (Docker + Caddy)

```bash
cp .env.example .env        # set DAV_HOST, AUTOCONFIG_HOST, AUTODISCOVER_HOST
docker compose up -d --build
```

The compose file runs the app and [Caddy](https://caddyserver.com) with automatic HTTPS ([Caddyfile](Caddyfile)). The app port is not published; only Caddy listens on 80/443. Open `https://<DAV_HOST>/`; on first start you are redirected to **/setup** to create the first administrator. Set `SETUP_TOKEN` in `.env` if the server is reachable before you finish setup.

Everything is stored in the `caldav-data` volume (`nullify-caldav.db` plus the `keys/` folder used to encrypt cookies and secrets — back both up together). The container runs as the non-root `app` user (UID 1654); when bind-mounting a host directory make it writable for that UID.

### Reverse proxy defaults

The app assumes it runs behind a TLS-terminating reverse proxy. It honours `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` from loopback and private networks (`10/8`, `172.16/12`, `192.168/16`, `100.64/10`, `fc00::/7`), which covers Caddy in the same Docker network or on the Docker host. Caddy sends these headers by default.

Using a different proxy, or one on a public address:

```nginx
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host $host;
    client_max_body_size 32m;
}
```

and set `ReverseProxy__KnownProxies` to its address. Basic authentication is used by DAV and SyncML clients, so never expose the app over plain HTTP.

### Running locally

```bash
dotnet run --project src/Nullify.CalDav
```

Data is written to `src/Nullify.CalDav/data` unless `Storage__DataDirectory` is set.

## Connecting clients

| Setting  | Value |
|----------|-------|
| CalDAV/CardDAV URL | `https://<host>/dav/` (or just the host name — `/.well-known/caldav` and `/.well-known/carddav` are supported) |
| SyncML URL | `https://<host>/syncml` (also `/sync` and `/funambol/ds`) |
| Username | your username **or email address** |
| Password | an app password from **Profile → App passwords**, or your account password if allowed |

Each user's profile page offers an **Apple configuration profile** (`.mobileconfig`) that adds CalDAV, CardDAV and — when a mail server is configured — the IMAP/SMTP account in one step.

The server implements the requests used by Apple Calendar/Reminders/Contacts, DAVx⁵ (with jtx Board / OpenTasks / Tasks.org for tasks and notes), Thunderbird, Evolution and KOrganizer; the automated tests exercise these request patterns but not the clients themselves.

## Migrating from another server

*Import* (in the top menu) copies data from an existing CalDAV/CardDAV server into the signed-in account:

1. Enter the old server's address (as used in your apps, e.g. https://example.com/baikal/dav.php/ or just https://dav.example.com/), username and password. Basic and Digest authentication are supported, so Baïkal works with either setting. For iCloud use https://caldav.icloud.com/ and https://contacts.icloud.com/ with an app-specific password.
2. The server finds your calendars and address books through /.well-known, current-user-principal and the home sets, following redirects. Scheduling inboxes and server-side subscriptions are skipped. Read-only collections (shared with you, generated) are listed but not ticked.
3. Choose what to import and start. The job runs in the background with progress on the page; each remote collection becomes a new local one with the same name, colour, description and content types, and items keep their file names and UIDs.
4. Run it again at any time before switching your devices: new items are added and changed items updated (matched by UID). Nothing is ever changed on the old server, and nothing local is deleted.

The old server's password is kept encrypted (data protection key ring) so the import can be re-run; *Remove source* deletes it. Regular users can only reach servers on the public internet unless *Allow outgoing requests to private networks* is enabled; administrators can always import from servers on the local network. Storage quotas apply to imported data.

## Using with DAVx⁵

Add an account with *Login with URL and user name*, using https://<host>/dav/ and an app password. DAVx⁵ then finds:

- your calendars and address books, plus anything shared with you directly or through a group;
- calendars of people who made you a **delegate**, via the proxy principals in your group memberships (enable *Refresh collection list* if they do not appear straight away);
- the read-only **Birthdays** calendar, the **Directory** address book and any **subscriptions**, all marked read-only;
- **WebDAV-Push**: install a UnifiedPush distributor (e.g. ntfy) and DAVx⁵ registers for push on each collection, so changes from other devices arrive within seconds instead of at the next poll;
- **invitations**: events you create with attendees are scheduled by the server (DAVx⁵ relies on this), and replies from local attendees update your copy automatically.

## Scheduling and invitations

With *Settings → Meeting invitations* enabled (the default), saving an event or task that has attendees triggers implicit scheduling (RFC 6638 calendar-auto-schedule):

| Action | Local attendee or organizer | External address |
|--------|-----------------------------|------------------|
| Organizer creates or significantly changes an event | Copy delivered to the attendee's default calendar and inbox | iMIP REQUEST email |
| Organizer removes an attendee or deletes the event | Attendee's copy marked CANCELLED | iMIP CANCEL email |
| Attendee accepts, declines or tentatively accepts (or deletes) | Organizer's copy updated with the new PARTSTAT | iMIP REPLY email |

- Delivery results are recorded as SCHEDULE-STATUS on the attendee (1.2 delivered, 1.1 emailed, 5.1 not delivered). Attendees keep their own alarms.
- Schedule-Tag / If-Schedule-Tag-Match are supported. Each user has inbox/ and outbox/ collections, and free/busy lookups can be POSTed to the outbox.
- Emails need an SMTP server (*Settings → Mail server*), a sender address and optionally SMTP credentials under *Meeting invitations*. *Send test email* checks the setup. Messages come from the sender address with Reply-To set to the organizer.
- Replies from external attendees go to the organizer's mailbox; the server does not read mailboxes, so those are processed by the organizer's mail or calendar app.

## Trash, quotas, subscriptions and public links

- **Trash**: deleted events, tasks, notes, contacts and whole collections are kept for the retention period (*Settings*, default 30 days), whichever client deleted them (CalDAV, CardDAV, SyncML or the web UI). Restore them from the *Trash* page.
- **Quotas**: set a default under *Settings* and per user on the user page. Clients see quota-used-bytes / quota-available-bytes (RFC 4331); uploads over quota get 507 Insufficient Storage. Generated collections and the trash do not count.
- **Subscriptions**: *Calendars → Subscribe to a calendar* adds a read-only calendar that the server downloads from an https:// or webcal:// feed on a schedule, so every device sees it. Requests to loopback, private and link-local addresses are blocked unless the admin allows them (protection against server-side request forgery).
- **Public links**: on a calendar's page, create a secret webcal:// link anyone can subscribe to, optionally showing only busy times. Links can be regenerated or disabled.

## Groups, delegation and generated collections

- **Groups** (*Admin → Groups*): share a calendar or address book with a group, and everyone in it sees it. Membership changes take effect immediately. Groups also appear as principals under /dav/groups/.
- **Delegation** (*Profile → Calendar delegates*): give someone read or read/write access to all your calendars. It is exposed through CalendarServer calendar-proxy-read / calendar-proxy-write principals, which Apple Calendar and DAVx⁵ understand.
- **Birthday calendar**: generated per user from the BDAY of contacts in their own address books and kept up to date.
- **Directory**: a read-only address book in every user's account listing everyone on the server, for autocompleting colleagues.
- Both can be switched off server-wide under *Settings*.

## IMAP passthrough authentication

Administrators can switch any account to **IMAP passthrough** (*Users → edit → Password verification*). The password is then verified by logging in to the IMAP server rather than against a local hash, for the web UI as well as for CalDAV, CardDAV and SyncML clients.

- The server-wide IMAP server is set under **Settings → Mail server**. Each account can override the host, port and security (TLS, STARTTLS or none).
- The IMAP login defaults to the account's email address, then its username, and can be overridden per account.
- Authentication uses `AUTHENTICATE PLAIN` when offered, otherwise `LOGIN`. Server certificates are validated unless *Accept invalid IMAP TLS certificates* is enabled.
- Successful checks are cached for five minutes, so a sync client does not cause an IMAP login per request. Failed attempts count towards the same brute-force throttle as local logins.
- App passwords and SSO keep working for IMAP accounts; the password change form is hidden for them.
- *Settings → Test IMAP connection* checks connectivity and TLS without logging in.

## Client autoconfiguration

Configure **Settings → Public address** (or the `PublicUrl` environment variable, set by the compose file) and **Settings → Mail server**. The app then answers:

| Client | Endpoint |
|--------|----------|
| Thunderbird (mail + CalDAV + CardDAV) | `/.well-known/autoconfig/mail/config-v1.1.xml`, `/mail/config-v1.1.xml` (on `autoconfig.<domain>`) |
| Outlook / Microsoft Autodiscover (POX) | `POST /autodiscover/autodiscover.xml` (on `autodiscover.<domain>`) |
| Outlook Autodiscover v2 | `/autodiscover/autodiscover.json?Protocol=AutodiscoverV1` |
| CalDAV/CardDAV discovery (RFC 6764) | `/.well-known/caldav`, `/.well-known/carddav` |
| Apple devices | `.mobileconfig` download on the profile page |

Mail settings are only published for the configured mail domains (all domains if none are listed). Exchange ActiveSync requests get the standard error response, so clients fall back to IMAP. Suggested DNS records:

```
autoconfig.example.com.          CNAME dav.example.com.
autodiscover.example.com.        CNAME dav.example.com.
_caldavs._tcp.example.com.       SRV 0 1 443 dav.example.com.
_caldavs._tcp.example.com.       TXT "path=/dav/"
_carddavs._tcp.example.com.      SRV 0 1 443 dav.example.com.
_carddavs._tcp.example.com.      TXT "path=/dav/"
_autodiscover._tcp.example.com.  SRV 0 1 443 autodiscover.example.com.
```

On the autoconfig and autodiscover host names the Caddyfile exposes only the metadata endpoints. If your apex domain is served elsewhere, redirect its `/.well-known/caldav` and `/.well-known/carddav` to the DAV host (see the commented block in the Caddyfile).

## SyncML

SyncML clients POST to `/syncml` using basic credentials (`syncml:auth-basic`) in the message header or an HTTP `Authorization` header. Server databases:

| Database URI | Content |
|--------------|---------|
| `contacts` (`card`, `addressbook`, `scard`) | First address book |
| `calendar` (`cal`, `agenda`, `scal`) | Events and tasks in the first calendar that supports events |
| `events` | Events only |
| `tasks` (`todo`, `stask`) | Tasks in the first calendar that supports tasks |
| `notes` (`memo`, `snote`, `journal`) | Notes stored as `VJOURNAL` |

Append `/<collection>` to choose a specific (own or shared) collection, e.g. `calendar/work` or `contacts/team`.

- Sync types 200–205 (two-way, slow, one-way and refresh in both directions), anchors, LUID mapping, multi-message packages honouring `MaxMsgSize`, `NumberOfChanges`, device information exchange (`Put`/`Get`/`Results`).
- Formats are chosen from the device's DevInf `Rx-Pref` (or the type of the items it sends): vCard 2.1 ↔ 3.0, vCalendar 1.0 ↔ iCalendar 2.0 (recurrence rules, alarms, all-day events, quoted-printable), and plain text or vNote 1.1 ↔ `VJOURNAL`. When a legacy device updates an item, the stored UID and properties vCalendar 1.0 cannot express (attendees, complex recurrence rules) are preserved.
- In slow syncs the server matches the client's items to existing ones by UID, or by name (contacts) or summary and start time (events), so nothing is duplicated.
- Known devices are listed on each user's profile page; forgetting a device forces a full sync next time.

## Configuration

All settings can be provided as environment variables (`Section__Key`) or in `appsettings.json`.

| Variable | Default | Purpose |
|----------|---------|---------|
| `Storage__DataDirectory` | `/data` in Docker | Location of the SQLite database and key ring |
| `PublicUrl` | *(empty)* | Public base URL for autoconfiguration (overridable in the UI) |
| `Setup__Token` | *(empty)* | If set, the first-run setup page requires this token |
| `ReverseProxy__Enabled` | `true` | Process `X-Forwarded-*` headers |
| `ReverseProxy__KnownNetworks` | private ranges | Comma-separated CIDR ranges of trusted proxies |
| `ReverseProxy__KnownProxies` | | Comma-separated trusted proxy IPs |
| `ReverseProxy__TrustAll` | `false` | Trust forwarded headers from any address |
| `ReverseProxy__ForwardLimit` | `1` | Number of proxy hops to process (`0` = unlimited) |
| `ReverseProxy__AllowedHosts` | | Restrict accepted forwarded host names |
| `PathBase` | | Serve under a sub-path, e.g. `/caldav` |
| `ASPNETCORE_HTTP_PORTS` | `8080` | Listening port inside the container |

Runtime settings (mail server, autoconfiguration, SyncML, OIDC, whether account passwords may be used by sync clients, hiding the password form) are edited by administrators under **Settings** and stored in the database.

### OpenID Connect

1. Register a confidential (or public + PKCE) client with your provider using the redirect URI shown on the Settings page (`https://<host>/signin-oidc`).
2. Under **Settings → OpenID Connect** enter the authority (issuer URL), client ID and secret, then enable it.
3. Choose how accounts are matched:
   - **Link from profile** (always available): a signed-in local user clicks *Link SSO account*.
   - **Auto-provision**: unknown SSO users get a new account with a default calendar and address book.
   - **Link by username**: an SSO identity is attached to an existing local account with the same username claim.
4. Optionally map administrator rights from a claim (e.g. `groups` = `caldav-admins`).

SSO applies to the web UI. Sync clients authenticate with app passwords, which SSO-only users create on their profile page. When the password form is hidden, `/account/login?local=true` remains available as a recovery path.

## Sharing

Owners share a calendar or address book with another user from its edit page, choosing **read only** or **read & write**. The shared collection appears automatically in the sharee's calendar/address book home (and can be targeted by SyncML as `calendar/<name>`), so it shows up in their clients without extra configuration. Clients see the access level through `DAV:current-user-privilege-set`; sharees may rename/recolour their own view, and deleting it from the client simply removes the share.

## Protocol support

- WebDAV (RFC 4918): `OPTIONS`, `GET`, `HEAD`, `PUT`, `DELETE`, `MOVE`, `PROPFIND`, `PROPPATCH`, `MKCOL`, ETags and `If-Match`/`If-None-Match`
- WebDAV ACL subset (RFC 3744): principals, `current-user-privilege-set`, `owner`, `principal-property-search`, `principal-search-property-set`, `principal-match`
- CalDAV (RFC 4791): `MKCALENDAR`, `calendar-query` (comp/prop/param filters, text-match, time-range incl. recurrence and alarms), `calendar-multiget`, `free-busy-query`, `supported-calendar-component-set`, UID conflict detection
- CardDAV (RFC 6352): `addressbook-query` (anyof/allof, match types, result limits), `addressbook-multiget`
- Extended MKCOL (RFC 5689), collection synchronization `sync-collection` (RFC 6578), `expand-property` (RFC 3253), CalendarServer `getctag`, Apple calendar colour/order
- Recurrence: `RRULE` (all frequencies, `BYxxx`, `BYSETPOS`, `COUNT`/`UNTIL`), `RDATE`, `EXDATE`, `RECURRENCE-ID` overrides; time zones via IANA/Windows IDs or embedded `VTIMEZONE`
- SyncML Data Synchronization 1.1/1.2 (XML and WBXML), IMAP4rev1 login (RFC 3501, STARTTLS, SASL PLAIN), Mozilla autoconfig 1.1, Microsoft Autodiscover POX/v2

Not implemented: scheduling (iTIP inbox/outbox, RFC 6638) and email invitations, CalendarServer sharing invitations (sharing is managed in the web UI), `calendar-data` partial retrieval/`expand`, `LOCK`/`UNLOCK`, `COPY`, Digest authentication. In SyncML: MD5 credentials (passwords are not stored in a reversible form), large-object (`MoreData`) splitting, server-alerted sync, filters and hierarchical sync.

## CI/CD

[.gitea/workflows/CalDav.yml](.gitea/workflows/CalDav.yml) runs on Gitea Actions for pushes to main, * tags, pull requests and manual runs:

1. docker build --target test . runs the whole test suite inside the SDK image (the runner needs no .NET).
2. Builds the image (version from the tag, otherwise  .0.0-ci.<sha>) and smoke-tests it: /health, the first-run setup page, and the DAV endpoints being gated until setup.
3. Except for pull requests, pushes git.nullify.net/simon/nullify-caldav:<sha>; main also pushes :latest, and a 1.2.3 tag pushes :1.2.3, :1.2 and :latest.
4. On main and tags, calls the Portainer webhook if one is configured.

Repository secrets: GITEADOCKERTOKEN (token with package write for simon) and, optionally, CALDAV_PORTAINER_WEBHOOK. To run the image from the registry, replace uild: . in docker-compose.yml with image: git.nullify.net/simon/nullify-caldav:latest.

## Development

```bash
dotnet test                                   # unit + integration tests
dotnet tool restore
dotnet ef migrations add <Name> -p src/Nullify.CalDav -o Data/Migrations
```

Migrations are applied automatically on start-up.

```
src/Nullify.CalDav
├── Dav/        WebDAV/CalDAV/CardDAV protocol: resolver, properties, filters, methods, reports
├── SyncMl/     SyncML session engine and WBXML codec
├── Formats/    iCalendar/vCard parser & writer, recurrence engine, time zones, legacy format conversion
├── Data/       EF Core entities, DbContext, migrations (SQLite)
├── Services/   Stores, users, credentials, IMAP, SMTP, scheduling, push, trash/quota, migration,
│               subscriptions, birthdays, directory, settings, OIDC, autoconfiguration
└── Pages/      Razor Pages management UI
```

### Dependencies

Only Microsoft packages are referenced: ASP.NET Core, `Microsoft.EntityFrameworkCore.Sqlite` and `Microsoft.AspNetCore.Authentication.OpenIdConnect`. The SQLite provider brings in the native SQLite bundle (`SQLitePCLRaw`) transitively, which is required to talk to SQLite at all. The IMAP and SMTP clients, Web Push encryption, WBXML codec and format converters are implemented in this project.
