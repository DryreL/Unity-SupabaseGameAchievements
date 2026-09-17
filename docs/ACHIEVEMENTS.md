# Achievement System

Cross-game achievements for games launched from this launcher. The reusable parts (database schema,
C# SDK, overlay) carry no launcher or company naming. DryreL Hub-specific wiring lives only in the
launcher, the `patreon-middleware` Edge Function and each game's bootstrap code.

| Piece | Location |
|---|---|
| Schema, RLS, RPCs | [`supabase/migrations/20260917000000_achievements.sql`](../supabase/migrations/20260917000000_achievements.sql) |
| Icon bucket | [`supabase/migrations/20260917000100_achievement_assets_bucket.sql`](../supabase/migrations/20260917000100_achievement_assets_bucket.sql) |
| Database tests (pgTAP) | [`supabase/tests/database/achievements.test.sql`](../supabase/tests/database/achievements.test.sql) |
| Patreon → Supabase session | `?action=supabase-session` in [`supabase/patreon-middleware/index.ts`](../supabase/patreon-middleware/index.ts) |
| Engine-agnostic C# core | [`sdk/achievements/Runtime/Core`](../Runtime/Core) (`DryreLHub.SupabaseGameAchievements`) |
| Unity host, overlay, transport | [`sdk/achievements/Runtime/Unity`](../Runtime/Unity) (`DryreLHub.SupabaseGameAchievements.Unity`) |
| Unity Localization adapter | [`sdk/achievements/Runtime/Localization`](../Runtime/Localization) |
| Tests (EditMode + PlayMode) | [`sdk/achievements/Tests`](../Tests) |
| Example integration | [`sdk/achievements/Samples~/ExampleIntegration`](../Samples~/ExampleIntegration) |
| Launcher setting → shared file | [`src-tauri/src/shared_settings.rs`](../src-tauri/src/shared_settings.rs), [`src/lib/settings/sharedSettings.ts`](../src/lib/settings/sharedSettings.ts) |
| TypeScript types | [`src/types/achievement.ts`](../src/types/achievement.ts) |
| Manifest exporter | [`scripts/export-achievement-catalog.mjs`](../scripts/export-achievement-catalog.mjs) |
| How to add achievements to a game | [`README.md`](../README.md) |

---

## 1. Architecture

```
GAME (Unity today; any engine later)
└── AchievementManager (static facade) ── AchievementSystem (instance)
    ├── AchievementCatalog            read-only manifest shipped with the build, pre-indexed
    ├── AchievementStore              unlocked + pending bitsets, account owner, crash-safe saves
    │   └── IAchievementStorage       FileAchievementStorage: temp file + fsync + File.Replace + .bak
    ├── AchievementSyncService        debounce, batches, backoff, reconciliation (driven by Tick)
    │   ├── IAchievementApiClient     SupabaseAchievementApiClient → 2 PostgREST RPCs
    │   ├── IAchievementAuthProvider  SupabaseSessionAuthProvider ← ISupabaseSessionSource (game's sign-in)
    │   └── IAchievementHttpTransport UnityWebRequestTransport
    ├── AchievementNotificationService queue + IAchievementNotificationSettingsProvider + localization
    └── IAchievementLocalizationProvider  Default (manifest text) | UnityLocalizationProvider
UNITY
    UnityAchievementManager (host: Tick, pause/focus/quit, reachability) · UnityAchievementOverlay (pooled toast)

LAUNCHER (Tauri)
    Settings toggle → set_achievement_notifications_enabled → <config dir>/DryreL Hub/shared-settings.json

SUPABASE
    games · achievements · user_achievements
    public.sync_achievements(game_id, ids[])  →  private.sync_achievements (SECURITY DEFINER)
    public.get_my_achievements(game_id)       (SECURITY INVOKER, RLS-scoped)
    Edge Function patreon-middleware ?action=supabase-session  (Patreon token → Supabase Auth session)
    Storage bucket achievement-assets (web icons only)
```

**Sources of truth.** Local state decides what the player sees *right now* and whether a toast
appears. The server is the persistent, cross-device record for signed-in players. Gameplay never
waits for the server.

---

## 2. Identity: why a Supabase Auth user exists per Patreon account

Players sign in with Patreon; before this feature nothing in the stack had a Supabase Auth user,
so `auth.uid()` and RLS could not work. The alternatives were worse:

- *Trust a client-supplied Patreon id* (what `SupabaseStatsManager` does today): any player can
  write anyone's rows.
- *Proxy every sync through an Edge Function* that re-verifies the Patreon token: one Patreon API
  call per sync, no RLS for future profile reads, and more code on the hot path.

So `patreon-middleware` gained one action (an action on the existing function, per the "only 2 Edge
Functions" rule):

```
POST /functions/v1/patreon-middleware?action=supabase-session
Authorization: Bearer <public key the launcher/Patreon plugin already send to patreon-middleware>
{ "patreon_access_token": "..." }
→ { access_token, refresh_token, expires_in, expires_at, token_type, user_id }
```

1. Calls Patreon `/api/oauth2/v2/identity` with the token. The Patreon user id comes from Patreon,
   never from the request.
2. Find-or-create an Auth user `patreon-<id>@<IDENTITY_EMAIL_DOMAIN>` with
   `app_metadata.patreon_id` (only the service role can write `app_metadata`). No email is ever
   sent.
3. Admin `generate_link` + `verify` → a normal Supabase session. If the user found for that address
   does not carry the matching `app_metadata.patreon_id` (someone self-registered it), the
   request is refused with `409 identity_conflict`.

Clients then refresh with the standard `/auth/v1/token?grant_type=refresh_token`; the function is
not on the refresh path. Sessions live **in memory only**, never in the achievement save file.

**Operational notes**
- Supabase Auth limits `verify` per IP. The function reads the auto-injected `SUPABASE_SECRET_KEYS`
  and forwards the caller's IP (`Sb-Forwarded-For`), so the limit applies per player rather than to
  the function's shared egress IP. Nothing to configure (custom secrets can't use the `SUPABASE_`
  prefix anyway).
- `IDENTITY_EMAIL_DOMAIN` (optional, default `patreon.users.dryrelhub.com`) must never change
  after launch, or existing players get new, empty accounts.
- If email sign-ups are not used anywhere else in the project, disable them (Auth → Providers →
  Email → "Allow new users to sign up"); the admin API used here is unaffected.

---

## 3. Database

### Tables

| Table | Purpose | Notes |
|---|---|---|
| `games` | One row per game | `slug` unique; `catalog_version` bumped automatically on any catalog change; `is_active = false` hides an unreleased catalog from clients |
| `achievements` | Canonical metadata | `UNIQUE(game_id, achievement_key)`, `UNIQUE(game_id, bit_index)`, `bit_index` 0–4095 |
| `user_achievements` | Unlock records | `PRIMARY KEY (user_id, achievement_id)`, `unlocked_at` only. No game id, no metadata, no JSON |

A `user_achievements` row is ~60 bytes plus its PK index entry. 10,000 players × 30 unlocks is
about 30 MB. The PK's leading `user_id` column serves every per-user query, so no other index is
added. An `achievement_id` index would only speed up cascades from hard deletes, which are blocked
(below).

### Invariants enforced by triggers

- `id`, `game_id`, `achievement_key`, `bit_index` are **immutable** (`UPDATE` raises).
- Achievements **cannot be hard-deleted** (a delete would free the bit index for reuse and
  silently remap old clients' bitsets). Retire instead: `is_retired = true`. A deliberate purge of a
  never-shipped game needs `set local achievements.allow_hard_delete = 'on'` in that transaction.
- Every insert/update/delete bumps `games.catalog_version`.

### Security

| Role | games / achievements | user_achievements | sync_achievements | get_my_achievements |
|---|---|---|---|---|
| anon | SELECT (active games only) | none | no EXECUTE | no EXECUTE |
| authenticated | SELECT (active games only) | SELECT own rows (`auth.uid() = user_id`) | EXECUTE | EXECUTE |
| service_role | all (bypasses RLS) | all | EXECUTE | EXECUTE |

- Grants are revoked explicitly. Supabase's default privileges grant everything to anon/authenticated
  on new public tables and functions, and RLS policies don't remove grants.
- No client role has INSERT/UPDATE/DELETE grants, and no write policies exist (defense in depth).
- `private.sync_achievements` is `SECURITY DEFINER`, `search_path = ''`, fully qualified, in a schema
  that is **not exposed** by the API. The exposed `public.sync_achievements` is a `SECURITY INVOKER`
  wrapper. `EXECUTE` is granted only to `authenticated`.
- No function accepts a user id. The user is always `auth.uid()`; a JWT without `sub` is rejected.

### RPC contract

`sync_achievements(p_game_id bigint, p_achievement_ids bigint[]) → jsonb`

```json
{ "accepted": [101, 102], "rejected": [999], "inserted": 1 }
```

- `accepted`: belongs to `p_game_id`, not retired, now stored (inserted now or earlier). The client
  clears these from its queue.
- `rejected`: unknown id, another game's id, or retired. **Permanent**; the client clears these too
  and logs a warning once.
- Duplicates and nulls in the array are ignored; `INSERT … ON CONFLICT DO NOTHING`; `unlocked_at`
  is the server's `now()`.
- Errors (HTTP 400, PostgREST `message`): `unknown_game`, `batch_too_large` (> 256 ids).
  Unauthenticated: 401/403.

`get_my_achievements(p_game_id bigint) → setof (achievement_id bigint, unlocked_at timestamptz)`,
RLS-scoped. Returns nothing for an inactive (unreleased) game, because its catalog is hidden from
clients. Sync still works for testers.

### Catalog administration (service_role only)

Neither `anon` nor `authenticated` has any write grant on `achievements` — retiring or deleting one
requires a service/secret key, via two RPCs (`20260918000000_achievement_admin.sql`), also reachable
from **Tools → DryreL Hub → Supabase Game Achievements → Manage Achievements** in Unity:

- `retire_achievement(p_achievement_id bigint)` — sets `is_retired = true`. Idempotent. This is the
  normal way to remove an achievement from play; its `bit_index` stays reserved forever.
- `delete_retired_achievement(p_achievement_id bigint)` — hard delete. Refuses unless the achievement is
  already retired *and* has zero rows in `user_achievements`, so it can only remove a mistake that never
  shipped and was never earned, never a real removal or anything a player has. On success it frees the
  `bit_index` for reuse (the only sanctioned way past `achievements_guard_identity`'s delete guard).

---

## 4. Synchronization semantics

**At-least-once delivery + idempotent storage.** The client may send the same ids any number of
times (lost responses, crashes, retries); the primary key makes that harmless. No exactly-once
machinery exists or is needed.

| Behavior | Implementation |
|---|---|
| Unlock | Sets unlocked + pending bits under a short lock, schedules a background save, raises the event. **0 requests.** |
| Debounce | Upload when no unlock happened for 1000 ms … |
| Threshold | … or immediately once 12 are pending |
| Batching | One RPC per ≤100 ids; a cycle loops until the queue is empty |
| In flight | At most one cycle. The batch is a snapshot; only ids the server confirmed *from that snapshot* are cleared. Unlocks made meanwhile stay pending and go in the next batch of the same cycle |
| Transient failure (offline, timeout, 408/429/5xx, unreadable 200) | Nothing cleared; exponential backoff 2 s → 5 min with ±20 % jitter |
| 401/403 | Refresh token once (`forceRefresh`), retry the batch; if still rejected wait 30 s. Pending never discarded |
| `unknown_game` | Manifest/backend mismatch: sync paused for the session, pending kept, error logged once |
| Other 4xx (e.g. RPC not deployed) | Retry after 10 min, pending kept |
| Sign-in | Transition detected → immediate upload of offline unlocks + reconciliation |
| Network back | `NotifyNetworkAvailable` (Unity host watches reachability) skips remaining backoff |
| Pause / focus loss | Save flushed, immediate sync requested |
| Quit | Final batch started; if the process exits first, the bits are still pending on disk |
| Reconciliation | One `get_my_achievements` per session/account (and after auth transitions). Merges set bits only, never clears one, clears pending bits the server already has, raises `ServerStateMerged`. **Never** a notification |
| Polling / Realtime | None |

**Accounts on a shared install.** The local state belongs to the last account that synchronized it.
Unlocks made while signed out, or while auth is temporarily unavailable, still belong to that
account (auth being down is not a sign-out). Never-claimed (guest) progress is adopted by the first
account that signs in. When a *different* account authenticates, the previous state is archived to
`account-<id>.bin` and the new account's archive (or a fresh state) becomes active, so progress is
never uploaded under the wrong account. An epoch counter makes results of requests started before a
switch no-ops.

---

## 5. Local persistence

`<persistentDataPath>/achievements/<game-slug>/active.bin` (+ `.bak`, transient `.tmp`, and
`account-<id>.bin` archives).

```
"ACHS" | format u16 | reserved u16 | game id i64 | catalog version i32 | owner uuid (16)
| N u16 | unlocked bitset (N) | pending bitset (N) | CRC-32
```

200 achievements take 92 bytes. Titles, descriptions and icons are never stored; the manifest has
them.

- **Atomic save:** write `.tmp` → `Flush(true)` (fsync) → `File.Replace(tmp, active, active.bak)`.
  Platforms without `File.Replace` use a copy/delete/move sequence that keeps a valid copy at every
  step.
- **Load:** primary → `.bak` if the primary is missing or fails its CRC (corrupt copy quarantined as
  `.corrupt-<time>`) → empty state if both are bad (reconciliation restores synced unlocks).
- **Newer format on disk** (player downgraded the game): loaded as read-only, never overwritten.
- **Unreadable** (file locked by antivirus): the load is retried before the first write and merged,
  so the stored state is never clobbered.
- **Write failure:** state stays in memory, retried every 5 s and on the next change.
- **Background writes:** coalesced on the thread pool (inline on WebGL). A superseded store (the game
  re-initialized achievements) is closed, so late results can't overwrite its successor's file.
- **Invariant repair:** pending bits without a matching unlocked bit are dropped on load.

---

## 6. Notifications and the launcher setting

A toast appears **only** for a genuine local locked → unlocked transition in `TryUnlock`. Not for
repeated unlocks, sync retries, sign-in uploads or reconciliation.

The launcher's **Settings → Achievements → "Show achievement notifications in games"** is
presentation-only. It is kept in `localStorage` like every other launcher setting and mirrored (on
change and at startup) by `set_achievement_notifications_enabled` to:

| OS | Path |
|---|---|
| Windows | `%APPDATA%\DryreL Hub\shared-settings.json` |
| macOS | `~/Library/Application Support/DryreL Hub/shared-settings.json` |
| Linux | `$XDG_CONFIG_HOME/DryreL Hub/shared-settings.json` (default `~/.config`) |

```json
{ "schema_version": 1, "achievement_notifications_enabled": true }
```

- Writer (Rust): unknown keys preserved, schema version never downgraded, temp + fsync + rename with
  retries, no rewrite when unchanged, writes serialized from the webview.
- Reader (C#): re-parsed only when mtime/size change, at most every 2 s, on focus, and before each
  toast. Missing file → enabled; malformed/partial/deleted → last known value; missing key or
  wrong type → default; extra keys ignored. Games work with the launcher closed.

The overlay (`UnityAchievementOverlay`): bottom-right, 24 px margins, "ACHIEVEMENT UNLOCKED",
icon, title, description; ease-in 0.25 s, visible until 2.5 s, fade/slide out by 3.0 s; FIFO queue
(no overlap); one reused view; sound once per toast; unscaled time; no raycast blocking; waits up to
0.2 s for localized text, then shows fallback text and swaps it in when ready.

---

## 7. Localization

Optional. `AchievementDefinition` always carries fallback `Title`/`Description`, plus optional
`LocalizationTable`/`TitleKey`/`DescriptionKey`.

- `DefaultAchievementLocalizationProvider`: manifest text.
- `UnityLocalizationProvider` (assembly compiles only when `com.unity.localization` is installed):
  resolves per field from the selected locale's string table, caches per locale, clears on
  `SelectedLocaleChanged`. Missing table/entry/empty value/load exception → that field's fallback.

Unlocking never waits for localization.

---

## 8. Icons

- **In game:** packaged assets, loaded with `Resources.Load<Sprite>(prefix + icon)` once per
  achievement and cached, including misses. Missing icon → fallback sprite or no icon; the toast
  still shows. No network.
- **Web/profile:** `achievement-assets/<game-slug>/<key>.webp` in Supabase Storage;
  `achievements.icon_path = '<game-slug>/<key>.webp'`. The exporter strips the extension for the
  manifest, so the game's resource path is `Resources/<prefix><game-slug>/<key>`.

---

## 9. Versioning and migration strategy

| Thing | Rule |
|---|---|
| `achievement_key`, `id`, `bit_index` | Immutable forever (trigger-enforced). Append new achievements with the next free bit index |
| Removing an achievement | `is_retired = true`. Old builds' uploads are rejected once and cleared; new manifests mark it `retired`, so `TryUnlock` refuses it |
| `catalog_version` | Auto-bumped; copied into the manifest and the save file for diagnostics |
| Manifest `formatVersion` | Readers accept ≤ their supported version and ignore unknown fields. Bump only for breaking changes; a newer format fails loudly at startup (achievements disabled, game unaffected) |
| Save format | `TryParse` switches on the version. To add v2: add `TryParseV2`, keep `TryParseV1` as the migration path, write v2 on next save. Unknown newer versions are never overwritten |
| Old build + newer save | Unknown bits are preserved (bitsets never shrink) and stay pending for the newer build |
| Old build + newer server catalog | Unknown server ids are ignored during reconciliation |
| Database | New migrations only; never edit an applied migration |
| Shared settings file | Add keys freely; never repurpose a key; bump `schema_version` only for breaking changes |

---

## 10. Failure handling

| Failure | Result |
|---|---|
| Unknown key | `false`, one warning per key, nothing queued |
| Unknown id from server | Ignored |
| Missing / malformed manifest | Achievements disabled with an error log; gameplay unaffected |
| Corrupt save | Backup restored, or fresh state + reconciliation |
| Disk write failure | In-memory state kept, retried |
| Not signed in | Local only, nothing sent, no auth lookups |
| Expired token | One refresh + retry, else wait; pending kept |
| Auth service down | `AuthUnavailable`, retry in 30 s; pending kept |
| Offline / timeout / 5xx | Backoff; pending kept |
| 4xx | Long retry; pending kept |
| Partial batch (some rejected) | Accepted + rejected cleared, the rest stays pending |
| Duplicate unlock | `false`, no event, no toast, no queue |
| Game id mismatch | Sync paused for the session; pending kept |
| Retired achievement uploaded by an old client | Rejected once, cleared, warned |
| Launcher settings unavailable | Last known value |
| Localization failure | Fallback text |
| Handler exception in game code | Logged; unlock unaffected |

---

## 11. Profile queries (future)

Everything is normalized; a profile page (RLS-scoped for the signed-in user, or through a
purpose-built definer function for public profiles) can read:

```sql
-- Per-game progress for the signed-in user
select g.slug, g.name,
       count(ua.achievement_id)                      as unlocked,
       count(a.id) filter (where not a.is_retired)   as total
  from public.games g
  join public.achievements a on a.game_id = g.id
  left join public.user_achievements ua
         on ua.achievement_id = a.id and ua.user_id = auth.uid()
 group by g.id
 order by g.name;

-- Recent unlocks
select a.achievement_key, a.title, a.icon_path, g.slug, ua.unlocked_at
  from public.user_achievements ua
  join public.achievements a on a.id = ua.achievement_id
  join public.games g on g.id = a.game_id
 where ua.user_id = auth.uid()
 order by ua.unlocked_at desc
 limit 20;
```

Hidden achievements: show `hidden and not unlocked` as "Hidden achievement" in the UI. The flag
is presentation-level, since the text also ships in game builds.

If "recent unlocks" ever gets slow for players with thousands of rows, add
`create index on user_achievements (user_id, unlocked_at desc)`. Not before.

---

## 12. Testing

| Suite | Run | Covers |
|---|---|---|
| Database | `supabase test db` | RLS enabled, grants, anon denial, own-rows isolation, definer config, idempotency, cross-game/retired/unknown rejection, `auth.uid()` attribution, missing `sub`, batch limit, invariants, catalog version |
| C# core (EditMode) | Unity Test Runner, EditMode | Catalog parsing, save format + CRC, atomic storage + recovery, unlock semantics, persistence, disk failures, newer/unreadable saves, catalog growth, debounce/threshold/batching, in-flight safety, lost responses, backoff, obsolete ids, unknown game, token refresh, auth outages, account switching, reconciliation races, re-initialization, notifications, localization, settings file, API contract |
| Unity (PlayMode) | Unity Test Runner, PlayMode | Toast queueing without overlap, one pooled view, sound once, disabled setting, missing icon, late localization |
| Launcher | `cargo test --lib shared_settings` | Atomic write, key preservation, malformed file, no-op rewrite, no temp leftovers |

The database tests use only core pgTAP assertions (`ok`, `is`, `throws_ok`, `lives_ok`). During
development they were also run against the migration in PGlite with a Supabase-like role/grant
stub; removing the explicit revokes or the game-ownership check makes 15 of them fail.

---

## 13. Deployment checklist

1. `supabase db push` (all three migrations), then `supabase test db`.
2. Redeploy `patreon-middleware` (`supabase functions deploy patreon-middleware`).
3. Edge Function secrets: none required. `IDENTITY_EMAIL_DOMAIN` is optional.
4. Insert the game and its achievements (see `README.md`), upload web icons.
5. Export the manifest into the game, add the package, wire the bootstrap, ship.

## 14. Unreal and other engines

The backend, RPC contract, manifest format, save format and shared settings file are
engine-neutral. An Unreal port reimplements `Runtime/Core` in C++ against the same contracts. The
two RPCs and the refresh-token grant are plain HTTPS + JSON; the state file is a documented binary
layout. No backend change is needed.

