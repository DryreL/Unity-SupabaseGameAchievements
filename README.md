# Supabase Game Achievements (com.dryrelhub.supabasegameachievements)

Offline-first achievements for games: instant local unlocks, crash-safe saves, batched idempotent
server sync, an optional unlock overlay, and optional Unity Localization. The design, security
model and failure handling are documented in [`docs/ACHIEVEMENTS.md`](docs/ACHIEVEMENTS.md).

```csharp
AchievementManager.TryUnlock("first_blood");   // instant, offline-safe, never awaits the network
AchievementManager.HasUnlocked("first_blood");
AchievementManager.GetDefinition("first_blood");
AchievementManager.AchievementUnlocked += e => Debug.Log(e.Definition.Title);
await AchievementManager.SyncAsync();          // optional: only when you need to know the result
```

| Assembly | Contents | Engine |
|---|---|---|
| `DryreLHub.SupabaseGameAchievements` | Catalog, store, storage, sync, auth, notifications, localization interfaces | none (`noEngineReferences`) |
| `DryreLHub.SupabaseGameAchievements.Unity` | `UnityAchievementManager`, `UnityAchievementOverlay`, `UnityWebRequestTransport`, settings, icons | Unity |
| `DryreLHub.SupabaseGameAchievements.Unity.Localization` | `UnityLocalizationProvider` (only compiled when `com.unity.localization` is installed) | Unity |

Requirements: Unity 2022.3+ (tested on 6000.3), `com.unity.nuget.newtonsoft-json`, uGUI.

---


## Installation

### Via Unity Package Manager (Git URL)

1. In Unity, open **Window > Package Manager**.
2. Click the **+** (plus) icon in the upper left corner.
3. Select **Add package from git URL...**
4. Paste the git repository URL:
   `	ext
   https://github.com/DryreL/Unity-SupabaseGameAchievements.git
   `
5. Click **Add**.

### Via Packages/manifest.json

Add the following line to your Packages/manifest.json under "dependencies":
`json
"com.dryrelhub.supabasegameachievements": "https://github.com/DryreL/Unity-SupabaseGameAchievements.git"
`

## Registering achievements for a new game

### 1. Create the catalog on the server

Run once per game in the Supabase SQL editor (as the project owner). Let the database assign ids.

```sql
insert into public.games (slug, name, is_active)
values ('my-game', 'My Game', false)          -- keep inactive until release
returning id;

insert into public.achievements
  (game_id, achievement_key, bit_index, display_order, title, description,
   hidden, icon_path, localization_table, title_key, description_key)
select g.id, v.*
  from public.games g,
  (values
    ('first_blood', 0, 0, 'First Blood', 'Defeat your first enemy.',
     false, 'my-game/first_blood.webp', 'ST_Achievements', 'first_blood_title', 'first_blood_description'),
    ('lava_walker', 1, 1, 'Lava Walker', 'Cross the lava field without falling.',
     false, 'my-game/lava_walker.webp', null, null, null)
  ) as v(achievement_key, bit_index, display_order, title, description,
         hidden, icon_path, localization_table, title_key, description_key)
 where g.slug = 'my-game';
```

Rules that keep old installs correct (the database enforces the first three):

- `achievement_key`, `id` and `bit_index` never change.
- New achievements take the **next unused** `bit_index`. Never reuse one, not even from a retired achievement.
- Don't delete achievements. Retire them: `update achievements set is_retired = true where ...`, or use
  **Tools → DryreL Hub → Supabase Game Achievements → Manage Achievements** (needs a Supabase
  service/secret key - a publishable key cannot retire or delete, on purpose). That window can also
  permanently delete an achievement, but only if it is already retired and no player has ever unlocked
  it (the server enforces both, regardless of what the window sends) - that path exists for a mistake
  that never shipped, not for real removals.
- Keys: lowercase `a-z 0-9 _ . -`, up to 64 characters. Bit indexes: 0–4095.
- Edits to titles/descriptions are fine; they bump `games.catalog_version` automatically.

Set `is_active = true` at release. (While inactive, clients can't read the catalog, and reconciliation
returns nothing, but syncing works.)

### Adding achievements later

Achievements are always created **in Supabase** (the database is the canonical catalog); the JSON in
the game is only an exported copy. The easiest way is **Tools → DryreL Hub → Supabase Game Achievements
→ Achievement Dashboard** (below); or use the SQL editor, or Table Editor → `achievements` → Insert row
(leave `id`, `created_at` empty). First find the next free bit index. It counts retired rows too,
so an index is never reused:

```sql
select coalesce(max(a.bit_index), -1) + 1 as next_bit_index
  from public.achievements a
  join public.games g on g.id = a.game_id
 where g.slug = 'my-game';

insert into public.achievements
  (game_id, achievement_key, bit_index, display_order, title, description,
   icon_path, localization_table, title_key, description_key)
select id, 'centurion', 2, 2, 'Centurion', 'Defeat 100 enemies.',
       'my-game/centurion.webp', 'ST_Achievements', 'centurion_title', 'centurion_description'
  from public.games where slug = 'my-game';
```

Then re-export the manifest (step 3), add the icon (step 4) and the string table entries, call
`TryUnlock("centurion")` somewhere, and ship a new build. Builds without the new manifest simply
don't know the achievement; nothing breaks.

### 2. Upload web icons (optional)

Upload `<key>.webp` (≤ 256 KB) to the `achievement-assets` bucket under `<game-slug>/`. These are
for the launcher and web profiles only. Games use packaged icons.

### 3. Get the manifest into the game

Two ways to ship a manifest, and they combine: the bundled file is always required as the offline
fallback; Remote Config (if you use it) can only override it, never replace it.

**3a. Bundled file (always do this one).** Two ways to produce it - same output either way:

- **In the Unity Editor:** **Tools → DryreL Hub → Supabase Game Achievements → Export Achievement
  Catalog**. Supabase URL/publishable key auto-fill from `Resources/PatreonConfig.asset` if present
  (button to redo it manually otherwise); type the game slug and pick an output path (suggested:
  `Assets/Resources/Achievements/achievements.json`, matching `PatreonAchievementsBootstrap`'s default
  load path) and click Export. For an inactive (`is_active = false`) game, put a secret/service key in
  the Export Key field for just that one export - it is never saved to disk.
- **From a script/CI**, e.g. the launcher repo:
  ```bash
  node scripts/export-achievement-catalog.mjs my-game ../MyGame/Assets/Achievements/achievements.json
  ```
  Uses `SUPABASE_PUBLISHABLE_KEY` from `.env`; `SUPABASE_EXPORT_KEY` in the shell for an inactive game.

Either way: re-run whenever the catalog changes and commit the JSON (output is deterministic, so an
unchanged catalog produces no diff). Assign it to `UnityAchievementManager`'s `Catalog Json` field (or
`Config.CatalogJson`) if you are not relying on the Manifest Resource Path fallback.

**3b. Remote Config override (optional).** Lets you push a title/description/new-achievement update
without a new build, on top of Unity Remote Config already configured in your project (this package
never starts a Remote Config fetch itself; something else in your project already does that, e.g. at
startup). Take the exact same exported JSON from step 3a and paste it as the value of a Remote Config
key (String type), e.g. `achievement_catalog_my-game`, in the Unity Cloud Dashboard for your linked
project's Remote Config environment. Set `UnityAchievementManager`'s `Remote Config Key` field (or
`Config.RemoteConfigKey`) to that same key name.

At startup, `RemoteConfigAchievementCatalogSource` compares the bundled manifest, its own on-disk cache
of the last Remote Config value that won, and whatever Remote Config has already fetched right now — the
highest `catalogVersion` for the same `gameId` wins, and it is cached for the next cold start. It never
regresses and never blocks: if Remote Config has nothing yet, the bundled manifest is used as normal.
Call `UnityAchievementManager.Instance.RefreshCatalogFromRemoteConfig()` after your project's Remote
Config fetch completes to hot-swap in a newer catalog without restarting the game (unlock/pending state
carries over unaffected — this is the same mechanism that already lets an older save load into a larger,
newer catalog). There is currently no automated way in this repo to push a value into Remote Config via
its Admin REST API — Unity's docs for that API are behind several redirects to a "legacy services" page
without a stable, verifiable endpoint spec, so this intentionally is not implemented; paste the value in
the dashboard, or write your own push script against Unity's current Remote Config Admin API docs.

### 4. Add packaged icons (or link to hosted ones)

**Packaged (the normal case).** Put sprites at `Assets/Resources/<prefix><icon>`, e.g. with prefix
`Achievements/`: `Assets/Resources/Achievements/my-game/first_blood.png` (the manifest's `icon` has no
extension). Unlike localization tables, this path matters: `Resources.Load` resolves it literally. This
is always offline-safe and instant — no network, ever.

**Two icon styles.** The toast can compose its icon in one of two ways, a game-wide choice:

1. **Combined** (default): each achievement's image already contains its own background. Nothing to set up.
2. **Layered**: one shared background image for every achievement, plus each achievement's own icon drawn
   on top, shrunk to leave a margin (`iconInset`, 0.18 of the background by default). Put the background
   at `Assets/Resources/<prefix><IconFolder>/background.png` (e.g. `Assets/Resources/Achievements/images/background.png`)
   and give each achievement a transparent icon.

The **Achievement Dashboard** has an *Icon Style* setting (under *Connection & files*) that shows both in
its previews and writes `"iconStyle": "layered"`, `"iconBackground": "images/background"` (and `"iconInset"`
when it is not the default) into the manifest it generates; a Combined game's manifest has none of these
fields. At runtime `UnityAchievementManager`'s *Icon Style* is **Auto** (follow the manifest), or force
Combined/Layered, and optionally assign the background `Sprite` or a Resources path directly (both are also
in `UnityAchievementManager.Config`). If the layered background cannot be found the game logs a warning and
uses combined icons, so a missing file never blanks the toast.

The style is stored in Supabase on the game row (`games.icon_style` = `combined` / `layered`,
`icon_background`, `icon_inset`; migration `20260921000000_games_icon_style.sql`, apply it with
`supabase db push`). The dashboard's **Push** sends a changed style (and **Pull** reads it), and every
export - the dashboard's Manifest button, the *Export Achievement Catalog* window and
`scripts/export-achievement-catalog.mjs` - writes the same manifest fields. Changing the style bumps
`catalog_version` like any catalog change. Against a database that has not applied the migration everything
keeps working as Combined.

**Hosted (dual support).** Set `icon_path` in Supabase to a full URL instead of a local path — anything
starting with `http://`, `https://`, or `www.` — and `ResourcesAchievementIconProvider` downloads it at
runtime instead of loading it from Resources. Mix and match freely: some achievements packaged, others
hosted, in the same catalog. This is the one place in the whole system that touches the network for
something other than sync: use it for icons you want to be able to swap without a new build (paired
with 3b's Remote Config catalog override, for example), not as the default — a hosted icon needs a live
network the first time it is shown, while a packaged one never does. The unlock and the toast itself
never wait for it: the toast appears immediately with a fallback icon and the real one fades in once the
download finishes (the same way localized text upgrades in place — see step 4b).

**Missing or unloadable icon (packaged file not found, or hosted download failed).** An already-shipped
build also has no way to have packaged art for an achievement that did not exist yet when it was built
(e.g. one added later purely through a Remote Config catalog update, step 3b). Either way,
`ResourcesAchievementIconProvider` never leaves a blank gap — it falls back, in order:

1. The sprite you set on `UnityAchievementManager.Config.FallbackIcon` (or passed to the provider's
   constructor), if any.
2. A `fallback` sprite sitting next to the achievement's own icon in the same `Resources` folder — e.g.
   `Assets/Resources/Achievements/my-game/fallback.png` for an icon at `my-game/first_blood`. Add one
   per game folder and every missing/broken icon in that game quietly uses it, no code required.
3. `DefaultAchievementIcon.GetOrCreate()` — a plain generated placeholder badge shipped with the package
   (not a file - built in code) so there is always a sane default even with zero setup.

Add real art at whichever tier matters to you; the ones below it stay as later safety nets.

### 4b. Add translations (optional)

Create a **String Table Collection** named `ST_Achievements` (Window → Asset Management →
Localization Tables → New Table Collection) with entries such as `first_blood_title` and
`first_blood_description`, and put `ST_Achievements` in the achievement rows' `localization_table`.

The **Achievement Dashboard** does this for you: once your achievements are in, press **Localize** in its
toolbar. If the string table `ST_Achievements` does not exist it is created (in
`Assets/Localization/Tables/ST_Achievements/`, one table per project Locale), and every achievement gets a
`<key>_title` and `<key>_description` entry holding its current title and description - e.g.
`clicked_on_a_link` gets `clicked_on_a_link_title` and `clicked_on_a_link_description`. The card's
*Localization (optional)* fields are the overrides: a table or key you typed there is used instead of the
default, and the empty ones are filled in with the defaults so the database row and the manifest point at
the entries (push afterwards, then regenerate the manifest). Existing entries are never overwritten, so
running Localize again only adds what is missing. Needs the `com.unity.localization` package, at least one
Locale and a Localization Settings asset; the table name and folder are under *Connection & files*.

The asset's folder does **not** matter: tables are looked up by collection name through Localization
Settings and Addressables, so `Assets/Localization/Tables/ST_Achievements/` (next to the game's other
`ST_*` tables) is just a convention. What matters is the exact name (case-sensitive), that the
collection is registered in Localization Settings (the Localization Tables window does this), and that
Addressables content is built with the player. A missing table, entry or translation falls back to
the manifest text for that field.

### 5. Add the package

`Packages/manifest.json`:

```json
"com.dryrelhub.supabasegameachievements": "https://github.com/DryreL/Unity-SupabaseGameAchievements.git"
```

or a git URL with `?path=/sdk/achievements`. Import the **Example Integration** sample from the
Package Manager for a starting point.

### 6. Bootstrap once

**If your game uses DryreL Hub's Unity Patreon Authenticator** (most DryreL Hub games do), no code is
needed at all:

1. Package Manager → this package → Samples → **Patreon Integration** → Import.
2. **Tools → DryreL Hub → Setup Achievements (Patreon) In Scene**. This adds a
   `PatreonAchievementsBootstrap` to the open scene, wired to `PatreonManager`'s real sign-in events.
3. Check the Inspector: Supabase URL/Key auto-fill from `Resources/PatreonConfig.asset` if present;
   the manifest auto-loads from `Resources/Achievements/achievements.json` if the **Manifest** field is
   left empty (matches the Export tool's default output path, step 3). Fill in anything that wasn't
   auto-filled, save the scene, done.

This sample requires that plugin to be in the project (it references it directly, on purpose — it is a
DryreL Hub product for DryreL Hub games) and is not imported by default, so projects without it are
never affected by it.

**Otherwise** (a different sign-in system, or no Patreon at all), wire it by hand — this is exactly what
`PatreonAchievementsBootstrap` above does internally, generalized to any identity system via two plain
delegates:

```csharp
using DryreLHub.SupabaseGameAchievements;
using DryreLHub.SupabaseGameAchievements.Unity;

var identity = new ExternalIdentitySessionSource(
    "https://PROJECT.supabase.co/functions/v1/patreon-middleware?action=supabase-session", // or your own endpoint
    supabasePublishableKey,
    UnityAchievementManager.Transport,
    hasIdentity: () => /* is the player signed in? */ false,
    getIdentityToken: () => /* a current identity access token, or null */ null,
    tokenFieldName: "patreon_access_token");
// Call identity.NotifyIdentityChanged() from your sign-in system's sign-in/sign-out callbacks.

var auth = new SupabaseSessionAuthProvider(supabaseUrl, supabasePublishableKey, UnityAchievementManager.Transport, identity);

UnityAchievementManager.Create(new UnityAchievementManager.Config
{
    CatalogJson = manifestTextAsset,
    SupabaseUrl = supabaseUrl,
    SupabasePublishableKey = supabasePublishableKey,  // publishable/anon key only
    SharedSettingsFolder = Application.companyName,   // or whatever your launcher's shared settings folder is
    IconResourcesPrefix = "Achievements/",
    UnlockSound = unlockClip,
}, auth, new UnityLocalizationProvider());           // or null without Unity Localization
```

Without a backend (leave URL/key empty) achievements are fully local. Without an auth provider
they unlock and queue offline, and sync after one is supplied and the player signs in.

### 7. Unlock from gameplay

```csharp
AchievementManager.TryUnlock("first_blood");
```

Call it as often as you like. Only the first successful call changes state, raises
`AchievementUnlocked` and shows the toast.

### 8. Unlock from Dialogue System for Unity (optional)

Import the **Dialogue System Integration** sample from the Package Manager (or copy its two files
next to your other sequencer commands, e.g. `Assets/Scripts/CustomDialogueScripts`). It has no
assembly definition on purpose, so it compiles into `Assembly-CSharp` where the Dialogue System
(`Assembly-CSharp-firstpass`) is visible.

1. Add **Achievement Lua Functions** to the Dialogue Manager object.
2. Use it from any dialogue entry:

| Where | Write |
|---|---|
| Script field | `UnlockAchievement("first_blood")` |
| Conditions field | `HasAchievement("first_blood")` or `HasAchievement("first_blood") == false` |
| Sequence field | `UnlockAchievement(first_blood)` (supports timing, e.g. `UnlockAchievement(first_blood)@2`) |

Re-running a node (loading a save, replaying a conversation) is safe: an already-unlocked achievement
returns false and shows nothing. To show the functions in the Script/Conditions dropdowns, add them to
a Dialogue System **Custom Lua Function Info** asset (`UnlockAchievement` and `HasAchievement`, one
String parameter, returning Bool).

---

## The notification overlay

Zero setup needed: `UnityAchievementManager` adds `UnityAchievementOverlay` itself and it builds its
own bottom-right panel in code the first time a toast is shown. Out of the box it already: slides
straight up from below the bottom edge of the screen, holds for ~2.25 s, slides straight back down,
never overlaps a second toast (they queue), plays the unlock sound once per toast, and never blocks
gameplay input (no `Graphic Raycaster`). Timings, margin, header text, sort order and the sound clip
are all serialized fields on `UnityAchievementOverlay` if you want to tweak them without touching art.

### Building your own visual (optional)

Only do this if you want different art/fonts (e.g. TextMeshPro) or a different animation. The overlay
never cares how the toast looks — it only calls `SetContent`/`SetText`/`SetVisibility` on whatever
`AchievementToastView` you give it.

1. In a scene, create **UI → Canvas** named e.g. `AchievementToastPrefab`. Set **Render Mode** to
   *Screen Space - Overlay*. Do **not** add a `Graphic Raycaster` — the toast must never eat clicks.
2. Add a child **Panel** (this is the part `SetVisibility` moves): anchor **bottom-right**
   (`anchorMin`/`anchorMax` = `(1, 0)`, pivot = `(1, 0)`), give it a fixed size (e.g. 440×104), and add
   a `Canvas Group` component to it (this is what `SetVisibility` fades).
3. Add children for **Icon** (`Image`), **Title** and **Description** (`Text` or `TextMeshProUGUI`) —
   any layout you like.
4. Add a script on the Canvas root that derives from `AchievementToastView`. Use the built-in one
   as-is if your fields are plain `Text`/`Image`, or copy it and swap `Text` for `TextMeshProUGUI` and
   override `SetContent`/`SetText`. **Do not override `SetVisibility`/`SetMargin`** unless you want a
   different animation — the base implementation already does the slide-up/slide-down described above,
   driven purely by your panel's own `RectTransform` height, so it adapts to whatever size you picked
   in step 2 automatically.
5. Assign the child references in the Inspector (Panel, Canvas Group, Icon, Title, Description),
   drag the whole Canvas into the project's `Assets` as a prefab, then assign that prefab to
   `UnityAchievementOverlay`'s **Toast Prefab** field.

If you want a *different* animation (e.g. fade only, or scale in), override `SetVisibility(float)`
yourself — `visibility` is driven every frame from 0 (hidden) to 1 (fully shown) and back, on
`Time.unscaledDeltaTime` (works while the game is paused). Sound and queuing stay exactly the same
either way; those live on `UnityAchievementOverlay`, not on the view.

## Editor tools

All under **Tools → DryreL Hub → Supabase Game Achievements**:

| Menu item | What it does |
|---|---|
| Achievement Dashboard | Create and edit every `achievements` column in one window (the **+** button adds one), autosaved in the project, pushed to Supabase from the Editor, and turned into the manifest file. See below. |
| Import Achievement Catalog | Parses an achievements.json manifest and imports it to Supabase via REST API (needs a service key) or generates an idempotent SQL script. |
| Export Achievement Catalog | Pulls one game's catalog from Supabase and writes the manifest JSON (see step 3a). |
| Manage Achievements | Retires or (with confirmation, only if already retired and never unlocked) permanently deletes an achievement. Needs a service/secret key. |
| Achievement Debug Window | Play Mode only. Lists every achievement in the running game with an Unlock button, plus Sync Now / Reconcile From Server, with live pending/unlocked counts - a quick way to exercise `AchievementManager` without writing test code. |

The Patreon sample adds one more once imported: **Setup Achievements (Patreon) In Scene** (see step 6).

### Achievement Dashboard

1. Open **Connection & files**, enter the Supabase URL (auto-filled from `PatreonConfig` if present), a
   service/secret key (kept for the session, never saved to disk) and the game slug, then **Connect & Pull**.
   If the game does not exist yet, **Create Game** adds it (inactive until you set `is_active = true`).
2. **+ Add Achievement** creates a card with the next free bit index (retired ones count) and an icon path of
   `<Icon Folder>/<key>`. The **Icon Folder** setting defaults to `images`, so with the `Achievements/` icon
   prefix the sprite goes to `Assets/Resources/Achievements/images/<key>.png`; rename the folder there if you
   prefer another name (paths of not-yet-pushed cards follow). Fill in key,
   title, description, icons, hidden/retired, display order and the optional localization fields. Fields are
   checked against the database constraints as you type.
3. Everything is **autosaved** to `ProjectSettings/DryreLHub.AchievementDashboard.json` (change it under
   *Data File*; commit it if your team shares the catalog). One file per game.
4. **Push** (toolbar, or per card) sends new entries as inserts and remembers the server id Supabase returns.
   Editing that achievement later and pushing again **updates the same row** - a card shows *Modified* until
   you do. `key` and `bit_index` are locked after the first push because the database refuses to change them.
   Pushing is Editor-only; a game build never contains a write path or a key.
5. **Manifest** writes the game's `achievements.json` (default `Assets/Resources/Achievements/achievements.json`)
   from the dashboard, with the catalog version Supabase reported after your last push. Entries that were
   never pushed have no server id yet, so they are left out (the window warns you).
6. **Pull** refreshes from Supabase; cards with unsent local edits keep those edits.

The dashboard never deletes: retire an achievement with its **Retired** checkbox, and use *Manage
Achievements* for the rare permanent delete.

## Threading

Call `AchievementManager` from the main thread. `UnityAchievementManager` pumps `Tick()` in
`Update`. Requests are created on the main thread and run on Unity's network threads; saves run on
the thread pool (inline on WebGL). Nothing blocks a frame on I/O except a rare account switch.

## Tests

Window → General → Test Runner. The package is test-enabled via `"testables": ["com.dryrelhub.supabasegameachievements"]`
in the project manifest. EditMode runs the core suite, PlayMode the overlay suite.


