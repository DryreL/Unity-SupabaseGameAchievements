# AI Agent Instructions — Supabase Game Achievements

This file exists so a future AI agent (or a human who is not the original author) can pick up this
system with zero prior context. If you are that agent: read this whole file before changing anything.
It documents **facts verified against the real code and a real Unity Editor**, not aspirations.

---

## 0. What this is, in one paragraph

An engine-agnostic, offline-first, cross-game achievement system. A game unlocks achievements locally
and instantly (no network, no auth required); unlocks queue and batch-sync to Supabase once a player is
signed in; the server is authoritative for cross-device state and idempotent against retries. The
reusable framework carries no company or game branding — it is generically usable by any Unity game,
and the Core assembly has no Unity dependency at all (a future Unreal port only needs to reimplement
`Runtime/Core`, not touch the database).

The full design rationale (why bit_index is immutable, why sync is batched, threading model, failure
handling for every case) is in **`docs/ACHIEVEMENTS.md`** in this repo — read it before making
architectural changes. This file is about *where things are* and *hard-won operational facts*, not the
design itself.

---

## 1. The three repositories involved

| Repo | Local path (on the machine this was built on) | Role |
|---|---|---|
| **This repo** — `Unity-SupabaseGameAchievements` | `D:\GithubRepos\SupabaseGameAchievements` | The canonical, portable SDK: C# runtime, Unity integration, Editor tools, Samples, docs. Distributed to games as a Unity Package Manager git dependency (`com.dryrelhub.supabasegameachievements`). |
| The launcher | Tauri desktop game launcher (COMPANY_NAME Games). Owns the Patreon OAuth flow, the `patreon-middleware` Supabase Edge Function, and (originally) the Supabase migrations. Its `docs/ACHIEVEMENTS.md` is now just a one-line redirect to this repo's copy — **this repo's `docs/ACHIEVEMENTS.md` is canonical**, not the launcher's. |
| A game | The pilot game using this package. References this repo via `Packages/manifest.json` as a **git URL** (not a local `file:` path), so local edits here do not reach that project until pushed to GitHub and re-resolved there. |

**Non-obvious and worth double-checking before you trust either copy:** a `supabase/` folder (migrations,
`patreon-middleware/index.ts`, pgTAP tests) now exists **in both this repo and the launcher repo**. This
was not a deliberate "repo A is canonical" decision documented anywhere — it appears to be a
consolidation that happened without a matching write-up. Before editing SQL or the Edge Function,
**diff both copies first** and ask the user which one is authoritative if they differ, rather than
assuming. Whichever you edit, the actual Supabase project only has the schema/functions once someone
runs `supabase db push` / `supabase functions deploy patreon-middleware` — editing a file in a repo does
not, by itself, change the live database.

---

## 2. Naming convention — read this before naming anything

**"DryreLHub" is the developer's own personal/studio handle**, used consistently across every reusable
Unity plugin in the YourGameName project (`DryreLHub.OfflineNetworkManager`, `DryreLHub.SupabaseStatsManager`,
`DryreLHub.UnityPatreonAuthenticator`, `DryreLHub.UniversalURL`).

Assembly names and C# namespaces in this package are `DryreLHub.SupabaseGameAchievements*`. Editor menu
items live under **`Tools > DryreL Hub > Supabase Game Achievements > ...`**; keep any new Editor tool
under that same submenu. Component-menu entries (`[AddComponentMenu]`) use `DryreL Hub/Supabase Game
Achievements/...` the same way.

Game-specific values (a real Supabase project URL, a real shared-settings folder
name, a real manifest) belong in the *consuming game's own project* or in a `Samples~/` folder the
consumer explicitly opts into (see §5).

---

## 3. Architecture at a glance

```
GAME (Unity today; Core has no Unity dependency, so another engine only needs to reimplement it)
├── AchievementManager (static facade)        ─┐
├── AchievementManagerProxy (safe MonoBehaviour │ three ways in — see §6
│    wrapper, auto-initializes, UnityEvents)    │
└── PatreonAchievementsBootstrap (Sample)      ─┘
        │
        ▼
AchievementSystem (per-catalog instance)
├── AchievementCatalog          read-only manifest (bundled TextAsset, optionally overridden by Remote Config)
├── AchievementStore            unlocked + pending bitsets, per-account, crash-safe file I/O
├── AchievementSyncService      debounce → batch → idempotent POST; reconciliation on sign-in
├── AchievementNotificationService + UnityAchievementOverlay   queued toast, never blocks unlock
└── IAchievementLocalizationProvider (optional; Unity Localization adapter is a separate assembly)

SUPABASE
├── games / achievements / user_achievements          (RLS: clients can only SELECT active-game rows /
│                                                        their own unlocks; zero write grants)
├── sync_achievements(game_id, ids[])                 idempotent batched unlock upload (authenticated)
├── get_my_achievements(game_id)                      reconciliation read (authenticated)
├── retire_achievement(id) / delete_retired_achievement(id)   catalog admin (service_role only)
└── patreon-middleware Edge Function, ?action=supabase-session
        turns a Patreon access token into a real Supabase Auth session (see §4) — this is the ONLY
        reason auth.uid()/RLS work at all in a Patreon-only product.
```

---

## 4. Identity: why a Supabase Auth user exists per Patreon account

The whole system authenticates players through Patreon; nothing in that flow ever produced a Supabase
Auth user, so `auth.uid()` (which every RLS policy and RPC in §5 relies on) would otherwise not exist.
`patreon-middleware` (an **action added to the existing Edge Function**, not a new function — see the
launcher's own "only 2 Edge Functions" rule) gained `?action=supabase-session`:

1. Verifies the caller's Patreon access token by calling Patreon's own `/identity` endpoint — the
   Patreon user id comes from Patreon, never trusted from the client.
2. Resolves the Supabase Auth user through `public.patreon_identities` (`patreon_id` unique →
   `user_id`), and only when there is none finds or creates `patreon-<id>@<IDENTITY_EMAIL_DOMAIN>`
   with `app_metadata.patreon_id` (service-role only field). It upserts the identity row (with
   `patreon_username`) on every sign-in. Do not identify a player by the synthetic e-mail: two
   copies of the function with different default domains once produced two accounts per Patreon
   user, which is why the achievements of "the same user" appeared under different `user_id`s.
3. Mints a session via admin `generate_link` + `verify` (no email is ever sent).

RLS and RPCs read the identity from `app_metadata.patreon_id` **only** (`jwt_patreon_id()`);
`user_metadata` is user-editable and must never be trusted. See the plugin's `AGENTS.md` §3.1 rules 8–9.

The client then refreshes that session the normal Supabase way; the Edge Function is not on the refresh
path. See `Runtime/Core/Auth/SupabaseSessionAuthProvider.cs` (generic caching/refresh) and
`Runtime/Core/Auth/ExternalIdentitySessionSource.cs` (generic "identity token → session" bridge — this
one is in Core, not the Patreon sample, because it does not itself reference Patreon at all; it takes
two delegates).

---

## 5. Critical rule: hard third-party dependencies go in `Samples~/`, never in `Runtime/` or `Editor/`

**This was gotten wrong once and empirically fixed — do not repeat the mistake.** `PatreonAchievementsBootstrap`
hard-references `DryreLHub.UnityPatreonAuthenticator` (by design; see §2, this is the sanctioned way for
a DryreL Hub game to wire achievements to Patreon). It was first shipped as an always-compiled
`Runtime/Patreon` + `Editor/Patreon` pair. Tested in a real, empty Unity project that had this package
but **not** the Patreon plugin: **the entire project failed to compile** — not just that one assembly.
Unity's Test Runner would not even launch, and unrelated tests failed too.

This is different from a reference to an assembly that is merely excluded by an unmet
`defineConstraints`/`versionDefines` (e.g. `DryreLHub.SupabaseGameAchievements.Unity.Localization`,
excluded when `com.unity.localization` is not installed): that case **is** silently dropped by Unity's
Bee build system, confirmed with a minimal two-asmdef repro, *as long as the referencing code never
unconditionally uses a type from it*. A reference to an assembly that does not exist **anywhere in the
project at all** is not the same case and does break everything.

**The fix, and the rule for any future hard dependency:** the whole Patreon module now lives under
`Samples~/PatreonIntegration/` (its own `Runtime/` and `Editor/` subfolders, own asmdefs), registered in
`package.json`'s `samples` array. `Samples~` (the trailing tilde is load-bearing) is invisible to Unity
until a consumer explicitly imports it via Package Manager → Samples, so a project without the Patreon
plugin is never touched by it. If you add another integration with a hard third-party dependency that
is not guaranteed to be present in every consumer's project, it goes in `Samples~/`, full stop.

---

## 6. Three ways an achievement system gets initialized in a game — know which one you're touching

1. **`UnityAchievementManager.Create(config, auth, localization)`** — the base, engine-generic entry
   point (`Runtime/Unity/UnityAchievementManager.cs`). Everything else calls this eventually.
2. **`PatreonAchievementsBootstrap`** (`Samples~/PatreonIntegration/Runtime/`) — a ready-made
   `MonoBehaviour` for DryreL Hub/Patreon games. Auto-fills Supabase URL/key from
   `Resources/PatreonConfig.asset`, wires real `PatreonManager` sign-in events, derives the launcher
   shared-settings folder from `Application.companyName` (no hardcoded studio name). Added to a scene
   via **Tools → DryreL Hub → Supabase Game Achievements → Setup Achievements (Patreon) In Scene**
   once the sample is imported.
3. **`AchievementManagerProxy`** (`Runtime/Unity/AchievementManagerProxy.cs`) — a defensive, generic
   safety net added later. `EnsureInitialized()` checks for an existing `UnityAchievementManager`, then
   tries a Resources-loaded bootstrap prefab, then reflectively calls
   `PatreonAchievementsBootstrap.EnsureCreated()` **if that type happens to be loaded** (reflection, so
   this generic-assembly file does not itself gain a hard Patreon reference — it degrades gracefully if
   the Patreon sample was never imported), then falls back to constructing a bare
   `UnityAchievementManager` from `Resources/Achievements/achievements.json` directly. Also exposes a
   static facade (`AchievementManagerProxy.TryUnlock(...)`) and `UnityEvent`-friendny instance methods
   for wiring straight from the Inspector (buttons, animation events) without any code. Also has a
   `[AddComponentMenu]` entry so it shows up in Unity's "Add Component" search.

If you are asked to change "how achievements get initialized," check which of these three the request
is actually about — they are not redundant, they are different levels of ceremony for different
consumers (raw API, Patreon-specific turnkey, defensive/UI-first).

---

## 7. Database (see `docs/ACHIEVEMENTS.md` §3 for the full design)

- `games`, `achievements` (catalog, immutable `bit_index`/`achievement_key`/`id` once shipped, no hard
  delete — trigger-enforced), `user_achievements` (tiny: `user_id`, `achievement_id`, `unlocked_at`).
- **Identity columns** (`20260920000000_patreon_identity.sql`): `public.patreon_identities`
  (`user_id` PK → `auth.users`, `patreon_id` unique, `patreon_username`; select-own only, written by the
  service role), and `user_achievements.patreon_id` / `patreon_username` / `game_slug`, filled by a
  trigger from `patreon_identities` and the catalog. `(patreon_id, game_id)` is unique, so one Patreon
  account can never hold two rows for a game. `user_achievements_readable` is a `security_invoker` view.
  `public.user_profiles` is **not** created by this repo: it belongs to the website (keyed by `id`).
- Every table's client grants are explicit (`revoke all ... ; grant select ...`) — Supabase's default
  privileges otherwise hand new tables to `anon`/`authenticated` automatically, and RLS policies alone
  do not revoke grants.
- **Client write path**: only `sync_achievements(game_id, ids[])`, a `SECURITY DEFINER` function in a
  `private` schema behind a `SECURITY INVOKER` public wrapper, `search_path = ''`, `auth.uid()` derived
  server-side (never a client-supplied user id). At-least-once client delivery + `ON CONFLICT DO
  NOTHING` server idempotency — retries never duplicate.
- **Catalog admin** (added 2026-09-18, `service_role` only, never callable with a publishable key):
  `retire_achievement(id)` (safe, idempotent, the normal way to remove an achievement from play) and
  `delete_retired_achievement(id)` (hard delete; the RPC itself refuses unless the row is already
  retired **and** has zero rows in `user_achievements` — the one sanctioned way past the
  `achievements_guard_identity` delete trigger). Reachable from **Tools → DryreL Hub → Supabase Game
  Achievements → Manage Achievements**.

---

## 8. C# SDK layout

| Path | Assembly | Notes |
|---|---|---|
| `Runtime/Core/` | `DryreLHub.SupabaseGameAchievements` | No Unity dependency (`noEngineReferences: true`). Catalog, store, sync, auth abstractions, notification queueing. Precompiled ref to `Newtonsoft.Json.dll`. |
| `Runtime/Unity/` | `DryreLHub.SupabaseGameAchievements.Unity` | `UnityAchievementManager`, `AchievementManagerProxy`, overlay, `UnityWebRequestTransport`, `RemoteConfigAchievementCatalogSource`, icon provider. |
| `Runtime/Localization/` | `...Unity.Localization` | `UnityLocalizationProvider`. `versionDefines`-gated on `com.unity.localization`; safe to reference unconditionally from other assemblies as long as they gate actual usage behind `#if ACHIEVEMENTS_UNITY_LOCALIZATION` (see §5's distinction). |
| `Editor/` | `...Editor` | Generic Editor tools (§10). References Core **and** Unity (`.Unity`) — remember to add that reference if a new tool touches `AchievementManager`/`UnityAchievementManager` (this was missed once and broke EditMode+PlayMode test discovery for the whole project until fixed). |
| `Samples~/ExampleIntegration/` | `...Samples` | Portable, delegate-based bootstrap example. Keep this one free of any specific identity plugin. |
| `Samples~/DialogueSystemIntegration/` | *(no asmdef — compiles into `Assembly-CSharp`)* | Lua functions + sequencer command for Pixel Crushers Dialogue System. Deliberately has no asmdef so it sees `Assembly-CSharp-firstpass` types the same way the Dialogue System itself does. |
| `Samples~/PatreonIntegration/` | `...Patreon` + `...Patreon.Editor` | See §5 and §6. |
| `Tests/Editor/` | `...Tests` | EditMode. References Core, Unity, **and Editor** now (needed once `AchievementManifestBuilderTests`/icon tests were added — they use `internal` types via `[InternalsVisibleTo]` on the Core/Editor `AssemblyInfo.cs` files). |
| `Tests/Runtime/` | `...Unity.Tests` | PlayMode (needs real `MonoBehaviour`/`GameObject`, e.g. the overlay's animation/queueing tests). |

---

## 9. Icons — dual local/URL support with a three-tier fallback

`icon_path` in Supabase (and the manifest's `icon` field) can be either a **local Resources-relative
path** (no extension — the historical/default behavior) or a **full URL** (`http://`, `https://`, or
`www.` — detected by prefix, `www.` gets `https://` prepended). Both exporters (the Node script in the
launcher repo and `AchievementCatalogExporterWindow` here) only strip the extension for local paths;
a URL keeps it. `ResourcesAchievementIconProvider` picks the mode per-achievement automatically.

A URL icon is downloaded via `UnityWebRequestTexture` **asynchronously**, never blocking the unlock or
the toast: `IAchievementIconProvider.GetIcon` returns a fallback immediately with `isFinal = false`,
`UnityAchievementOverlay` shows the toast right away and swaps the real icon in once
`GetIconAsync` resolves — the same upgrade-in-place pattern already used for localized text.

Whenever an icon cannot be resolved (missing local file, failed download, achievement added later via
Remote Config with no matching packaged art), the fallback chain is: (1) an explicitly configured
`Sprite` (`Config.FallbackIcon`/constructor param), (2) a `fallback` resource sitting in the *same
Resources folder* as the achievement's own icon (e.g. `Achievements/YourGameName/fallback.png` for an icon
at `YourGameName/lava_walker` — automatic, zero code), (3) `DefaultAchievementIcon.GetOrCreate()`, a small
circular badge **generated in code**, not a shipped asset. Every tier is wrapped in try/catch — a
throwing custom loader must still degrade to the next tier, not crash icon resolution (this was a real
bug, caught by `ResourcesAchievementIconProviderTests`, fixed 2026-09-18).

`ResourcesAchievementIconProvider` takes optional `localLoader`/`urlDownloader` delegates purely for
testability (default to `Resources.Load<Sprite>` and a real network fetch) — same DI-for-testing pattern
used everywhere else in this codebase (`IAchievementClock`, `IAchievementHttpTransport`,
`RemoteConfigAchievementCatalogSource`'s `liveValueReader`). Use them, don't add a different pattern.

---

## 10. Remote Config

`RemoteConfigAchievementCatalogSource` reads a manifest override via reflection against either
`Unity.Services.RemoteConfig.RemoteConfigService` or the legacy `Unity.RemoteConfig.ConfigManager` —
whichever is present, no hard package dependency either way, mirroring this exact project's own
pre-existing pattern (`Assets/Scripts/Sentry/SentryRemoteConfigDsn.cs`,
`PatreonPromotionAccessManager.cs` in `<YourGameName>`). It never starts a fetch itself. Tries
`GetJson(key, "{}")` first (Remote Config's native "Json" value type), falls back to `GetString(key,
"")` (a "String"-typed key) — so either value type works for the key you create in the dashboard.
Highest `catalogVersion` for the same `gameId` wins among {bundled, on-disk cache, live value}; a
result is cached to disk so a cold start before the project's own fetch completes still benefits.
`PatreonAchievementsBootstrap`'s default key name is `achievement_catalog` — match that in the
dashboard, or change the Inspector field.

**No automated way to push a manifest into Remote Config exists.** Unity's Admin REST API docs redirect
through several "legacy services" pages with no stable, verifiable spec found during research — rather
than guess an endpoint shape, this was deliberately left as a manual dashboard paste. Do not invent an
API call here without verifying it against current Unity documentation first.

---

## 11. Editor tools — menu map

All under **Tools → DryreL Hub → Supabase Game Achievements**:

| Item | File | Notes |
|---|---|---|
| Achievement Dashboard | `Editor/AchievementDashboardWindow.cs` (UI + PostgREST) and `Editor/AchievementDashboardModel.cs` (data, validation, payloads, merge, manifest) | Editor for every `achievements` column. Working copy autosaved to `ProjectSettings/DryreLHub.AchievementDashboard.json`; each entry stores its server `id` after the first push, so later edits PATCH that row (mutable columns only - the identity trigger rejects `key`/`bit_index` changes). "Modified" is derived by comparing the current fields with `SyncedSnapshot`, not a dirty flag. Service key is session-only like the other windows; `sb_secret_` keys go in `apikey` only, JWT keys also in `Authorization` (same rule as the exporter). New entries get `icon_path = <IconFolder>/<key>` (`IconFolder` defaults to `images`, relative to the runtime icon prefix `Achievements/`); that auto path follows key/folder edits only for never-pushed entries. Localize button: `DashboardData.BuildLocalizationPlan()` (overrides, else `<key>_title`/`<key>_description` in `DefaultLocalizationTable`) is handed to `AchievementLocalizationBridge.Sync`, a hook in the Editor assembly that `Editor/Localization/` (own asmdef, `versionDefines`-gated on `com.unity.localization`, compiled against 1.5.13 only) assigns from an `[InitializeOnLoad]` constructor - the Editor assembly itself never references Unity Localization (same rule as §5). It only adds missing entries, never overwrites text. Never autosaves over a data file it failed to load. Logic is tested in `Tests/Editor/AchievementDashboardModelTests.cs`; the IMGUI/network layer was compile-checked only, not driven in a live Editor. |
| Export Achievement Catalog | `Editor/AchievementCatalogExporterWindow.cs` | Pulls a game's catalog from Supabase, writes the manifest. Auto-fills Supabase URL/key from `PatreonConfig` via **reflection** (this assembly must stay portable — no hard Patreon reference here, unlike the Patreon sample). |
| Manage Achievements | `Editor/AchievementManagementWindow.cs` | Retire/delete via the service-role-only RPCs (§7). |
| Achievement Debug Window | `Editor/AchievementDebugWindow.cs` | Play Mode only. Per-achievement Unlock button, Sync Now / Reconcile buttons, live counts. Also hosts **Clear Local Achievements (Reset)**, which deletes the on-disk save under `Application.persistentDataPath/achievements` and calls `UnityAchievementManager.ResetLocalState()` if playing. |
| Setup Achievements (Patreon) In Scene | `Samples~/PatreonIntegration/Editor/` | Only exists once that sample is imported (§5/§6). |

---

## 12. How this was verified without a .NET SDK, Docker, or a full YourGameName rebuild

These tricks matter because the dev machine had **no `dotnet` CLI, no running Docker daemon**, and
touching the real `<YourGameName>` project's `Packages/manifest.json` mid-session was avoided to not
disrupt the user's own Editor session.

- **C# core, without a .NET SDK:** Unity's own bundled Roslyn compiler
  (`<Unity>/Editor/Data/DotNetSdkRoslyn/csc.dll`, run via `<Unity>/Editor/Data/NetCoreRuntime/dotnet.exe`)
  compiles against `<Unity>/Editor/Data/NetStandard/ref/2.1.0/netstandard.dll`, and a tiny hand-rolled
  reflection-based NUnit runner (`Runner.cs` in the scratch build folder) executes the resulting test
  assembly. Use a `.rsp` response file for the reference list — the Unity install path contains a space
  and breaks naive shell word-splitting.
- **Unity/Editor-dependent code (needs real `UnityEngine`/`UnityEditor` assemblies, or real
  `MonoBehaviour`/`GameObject` for PlayMode tests):** the reflection-runner trick above stops working
  once code needs the actual Unity runtime initialized. Instead, a **throwaway Unity project** was
  created once (`Packages/manifest.json` pointing at this repo via `file:D:/GithubRepos/SupabaseGameAchievements`)
  and driven with `Unity.exe -batchmode -runTests -testPlatform EditMode|PlayMode`. This is also how the
  §5 blast-radius bug was discovered — a truly empty project without the Patreon plugin installed.
- **To test against the real YourGameName-specific assemblies** (`DryreLHub.UnityPatreonAuthenticator.dll`,
  etc.) without touching the live `<YourGameName>` project's package resolution: point `csc.exe` at the
  already-compiled DLLs sitting in `<YourGameName>/Library/ScriptAssemblies/` directly. Unity had
  already resolved this package there once (as a git dependency), so those DLLs exist and are safe to
  read-reference without triggering a reimport.
- **SQL/RLS/RPCs, without Docker (so no local Supabase, no `supabase test db`):** `@electric-sql/pglite`
  (an in-process Postgres, pure npm install) with a small stub schema (`anon`/`authenticated`/
  `service_role` roles, a minimal `auth.users` table, an `auth.uid()` shim reading
  `request.jwt.claims`, Supabase's default-privileges behavior) runs the real migration files and either
  the real pgTAP test file (core assertions only: `ok`/`is`/`throws_ok`/`lives_ok`, shimmed) or ad-hoc
  scripts. **Role switches must happen in the same `db.exec()` call as the query they gate** — `SET
  LOCAL` does not survive across separate `.exec()`/`.query()` calls in PGlite, since each is its own
  implicit transaction; a test written across two separate calls will silently run as the unrestricted
  connecting role and give a false "not blocked" result. (Caught this exact mistake once — got "authenticated
  could retire" until the role switch and the query were combined into one `db.exec()` string.)
- **Mutation testing** (deliberately breaking a guard to confirm the test actually catches it, not just
  passes) was used repeatedly and did catch real bugs — do this before trusting a new test suite,
  especially anything touching grants/RLS or a fallback chain.

---

## 13. Hard-won gotchas (each one cost real debugging time — don't rediscover them)

- **`PatreonManager.PatreonUser` is `internal`** to `DryreLHub.UnityPatreonAuthenticator`'s assembly.
  Subscribing to `PatreonManager.OnUserAuthenticated` (an `Action<PatreonUser>`) from outside that
  assembly with a named method and an explicit `PatreonUser` parameter fails with **`CS0246` ("type not
  found")**, not `CS0122` ("inaccessible") — misleading, reads like the type doesn't exist. Fix: use an
  inferred-type lambda (`PatreonManager.OnUserAuthenticated += _ => ...;`), which never needs to name
  the type. Also: there is then no delegate reference to pass to `-=`, so don't try to unsubscribe it —
  both `PatreonManager` and any bootstrap using it are `DontDestroyOnLoad` singletons for the app's
  whole lifetime, so this is harmless.
- **A file compiled under `Assets/Plugins/<X>/` with its own `.asmdef` does NOT go into
  `Assembly-CSharp-firstpass.dll`** — it compiles into its own named assembly
  (`DryreLHub.UnityPatreonAuthenticator.dll`, in this case). Only `Assets/Plugins/...`Files *without*
  an asmdef fall into the generic firstpass bucket. Reference the specific named assembly.
- **Newtonsoft `JToken`/`JValue` do not compare equal to raw primitives via `NUnit.Assert.AreEqual`** —
  `Assert.AreEqual(1, jObject["x"])` fails with a type-mismatch message even when the underlying value
  is `1`. Use `jObject.Value<int>("x")` (or cast) before asserting.
- **`Editor/*.cs` files calling into the `.Unity` assembly need `.Unity` added to the Editor asmdef's
  `references`** — this is not automatic just because Core is referenced. Missing it doesn't just break
  that one Editor script; it fails the whole project's script compilation (same class of problem as §5,
  but this one **is** safe to reference unconditionally since `.Unity` has no third-party hard
  dependency of its own).
- **The `Edit` tool's exact-string matching can silently fail on a leading UTF-8 BOM** in a `using`
  line at the very top of a file — if a `using X;\nusing Y;` old_string match fails inexplicably, check
  for a BOM character before the first `using`, or match a string that starts one line later.
- **PowerShell/bash quoting inside a generated one-liner breaks on straight apostrophes** (e.g. "Unity's
  own build system") when embedded in a `node -e '...'` single-quoted script — write the JS to a real
  `.mjs`/`.cjs` scratch file and run that instead of trying to escape apostrophes inline.

---

## 14. Current status (accurate as of last edit to this file, not a permanent fact)

- This repo's `origin/main` is up to date with the commits through "Clear Local Achievements (Reset)
  button added" — check `git log`/`git status` yourself; do not trust this list once more work has
  happened. Anything shown as locally modified/untracked by `git status` when you read this has **not**
  been pushed.
- `<YourGameName>` references this package by **git URL**, not `file:`, so local edits here need a
  push (and the consuming project needs to re-resolve packages) before they take effect there.
- Live database (2026-09-20/21): `20260920000000_patreon_identity.sql` was applied through the SQL editor and
  the improved `patreon-middleware` deployed (identical in this repo and the plugin). The migration
  `20260918000000_patreon_profiles.sql` is a stub. Duplicate accounts from the old default-domain bug were
  merged/removed. The website's hardening (tokens moved to `patreon_credentials`, `public_profiles`, ...) is
  documented in `Viznity.Github.io/AGENTS.md` §4.4.
- The `supabase/` folder existing in two repos (§1) has not been reconciled — verify before assuming
  either is the sole source of truth, and check whether the actual Supabase project has all pending
  migrations applied (`supabase db push` was **not** run as part of any of this work; migration files
  existing on disk does not mean the live database has them).
- No automated CI runs any of the verification in §12 — it was all done ad hoc in an agent session.
  If reproducibility matters, that would be worth setting up properly (GitHub Actions + a real Unity
  license, or at minimum the PGlite-based SQL tests, which need no Unity license at all).

---

## 15. Where to look next

- **Design and full behavioral contract** (batching, threading, every failure mode, versioning rules):
  `docs/ACHIEVEMENTS.md` in this repo.
- **Per-game setup, step by step:** `README.md` in this repo.
- **Launcher-side context** (why Patreon-only auth, the shared-settings file contract, the "only 2 Edge
  Functions" rule): `COMPANY_NAMEDesktopApp/AGENTS.md` Section 9, and its own `docs/ACHIEVEMENTS.md` (which
  just redirects here — read this repo's copy).
