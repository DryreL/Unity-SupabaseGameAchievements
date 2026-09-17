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
- Don't delete achievements. Retire them: `update achievements set is_retired = true where ...`.
- Keys: lowercase `a-z 0-9 _ . -`, up to 64 characters. Bit indexes: 0–4095.
- Edits to titles/descriptions are fine; they bump `games.catalog_version` automatically.

Set `is_active = true` at release. (While inactive, clients can't read the catalog, and reconciliation
returns nothing, but syncing works.)

### Adding achievements later

Achievements are always created **in Supabase** (the database is the canonical catalog); the JSON in
the game is only an exported copy. Use the SQL editor, or Table Editor → `achievements` → Insert row
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

**3a. Bundled file (always do this one).**

```bash
node scripts/export-achievement-catalog.mjs my-game ../MyGame/Assets/Achievements/achievements.json
```

(From the launcher repo. Uses `SUPABASE_PUBLISHABLE_KEY` from `.env`. For an inactive game set
`SUPABASE_EXPORT_KEY` to a secret key in your shell. Never commit that key or ship it.) Re-run whenever
the catalog changes and commit the JSON. The output is deterministic, so an unchanged catalog produces
no diff. Assign it to `UnityAchievementManager`'s `Catalog Json` field (or `Config.CatalogJson`).

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

### 4. Add packaged icons

Put sprites at `Assets/Resources/<prefix><icon>`, e.g. with prefix `Achievements/`:
`Assets/Resources/Achievements/my-game/first_blood.png` (the manifest's `icon` has no extension).
Unlike localization tables, this path matters: `Resources.Load` resolves it literally. Icons are always
loaded from packaged Resources, never from Remote Config or the network, even when 3b is used.

**Missing icon (e.g. an old build + an achievement added later via Remote Config).** An already-shipped
build has no way to have packaged art for an achievement that did not exist yet when it was built. When
`ResourcesAchievementIconProvider` cannot load an icon — missing file, bad path, anything — it never
leaves a blank gap; it shows a fallback instead, in this order: your own fallback sprite if you set
`UnityAchievementManager.Config.FallbackIcon` (or passed one to the provider's constructor), otherwise
`DefaultAchievementIcon.GetOrCreate()` — a plain generated placeholder badge shipped with the package so
there is always a sane default with zero setup. Give it your own fallback sprite once you have real art
for it; the generated one is intentionally plain.

### 4b. Add translations (optional)

Create a **String Table Collection** named `ST_Achievements` (Window → Asset Management →
Localization Tables → New Table Collection) with entries such as `first_blood_title` and
`first_blood_description`, and put `ST_Achievements` in the achievement rows' `localization_table`.

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

```csharp
using DryreLHub.SupabaseGameAchievements;
using DryreLHub.SupabaseGameAchievements.Unity;

var identity = new ExternalIdentitySessionSource(
    "https://PROJECT.supabase.co/functions/v1/patreon-middleware?action=supabase-session",
    supabasePublishableKey,
    UnityAchievementManager.Transport,
    hasIdentity: () => PatreonManager.Instance != null && PatreonManager.Instance.IsUserAuthenticated(),
    getIdentityToken: () => PatreonManager.Instance != null ? PatreonManager.Instance.GetValidAccessToken() : null,
    tokenFieldName: "patreon_access_token");
PatreonManager.OnUserAuthenticated += _ => identity.NotifyIdentityChanged();
PatreonManager.OnUserSignedOut += identity.NotifyIdentityChanged;

var auth = new SupabaseSessionAuthProvider(supabaseUrl, supabasePublishableKey, UnityAchievementManager.Transport, identity);

UnityAchievementManager.Create(new UnityAchievementManager.Config
{
    CatalogJson = manifestTextAsset,
    SupabaseUrl = supabaseUrl,
    SupabasePublishableKey = supabasePublishableKey,  // publishable/anon key only
    SharedSettingsFolder = "DryreL Hub",           // launcher's shared settings folder
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

## Threading

Call `AchievementManager` from the main thread. `UnityAchievementManager` pumps `Tick()` in
`Update`. Requests are created on the main thread and run on Unity's network threads; saves run on
the thread pool (inline on WebGL). Nothing blocks a frame on I/O except a rare account switch.

## Tests

Window → General → Test Runner. The package is test-enabled via `"testables": ["com.dryrelhub.supabasegameachievements"]`
in the project manifest. EditMode runs the core suite, PlayMode the overlay suite.


