using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>Where a dashboard entry stands relative to its Supabase row.</summary>
    internal enum DashboardSyncState
    {
        /// <summary>Never sent to Supabase (no server id yet).</summary>
        New,

        /// <summary>Sent before, but edited locally since.</summary>
        Modified,

        /// <summary>Identical to the Supabase row as of the last push/pull.</summary>
        Synced,
    }

    /// <summary>
    /// One achievement as edited in the dashboard: exactly the columns of <c>public.achievements</c>,
    /// plus the bookkeeping needed to update the same row later instead of inserting a duplicate.
    /// </summary>
    internal sealed class DashboardAchievement
    {
        /// <summary>Server <c>achievements.id</c>. 0 until the first push (or pull) assigns one.</summary>
        public long Id { get; set; }

        // Immutable once the row exists: the database trigger rejects any change to these.
        public string Key { get; set; } = "";
        public int BitIndex { get; set; }

        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string IconPath { get; set; } = "";
        public string IconUrl { get; set; } = "";
        public bool Hidden { get; set; }
        public bool Retired { get; set; }
        public int DisplayOrder { get; set; }
        public string LocalizationTable { get; set; } = "";
        public string TitleKey { get; set; } = "";
        public string DescriptionKey { get; set; } = "";

        /// <summary>
        /// Serialized mutable fields as of the last push/pull; null while the entry has never been synced.
        /// Comparing it with the current fields is what makes an entry "Modified" - there is no dirty flag
        /// that an edit could forget to set.
        /// </summary>
        public string SyncedSnapshot { get; set; }

        [JsonIgnore] public bool Expanded { get; set; }

        [JsonIgnore]
        public DashboardSyncState State =>
            Id <= 0 ? DashboardSyncState.New :
            SyncedSnapshot == BuildSnapshot() ? DashboardSyncState.Synced : DashboardSyncState.Modified;

        /// <summary>True once the identity columns are locked by the database.</summary>
        [JsonIgnore] public bool IsIdentityLocked => Id > 0;

        public string BuildSnapshot() => BuildUpdatePayload().ToString(Formatting.None);

        public void MarkSynced() => SyncedSnapshot = BuildSnapshot();

        /// <summary>Only the columns the database allows to change: what a PATCH may send.</summary>
        public JObject BuildUpdatePayload() => new JObject
        {
            ["title"] = Title,
            ["description"] = Description ?? "",
            ["icon_path"] = NullIfEmpty(IconPath),
            ["icon_url"] = NullIfEmpty(IconUrl),
            ["hidden"] = Hidden,
            ["is_retired"] = Retired,
            ["display_order"] = DisplayOrder,
            ["localization_table"] = NullIfEmpty(LocalizationTable),
            ["title_key"] = NullIfEmpty(TitleKey),
            ["description_key"] = NullIfEmpty(DescriptionKey),
        };

        /// <summary>A full row for a POST: the identity columns plus everything a PATCH would send.</summary>
        public JObject BuildInsertPayload(long gameId)
        {
            var payload = BuildUpdatePayload();
            payload["game_id"] = gameId;
            payload["achievement_key"] = Key;
            payload["bit_index"] = BitIndex;
            return payload;
        }

        /// <summary>
        /// The same shape Supabase returns for a row, so <see cref="AchievementManifestBuilder"/> can consume
        /// the dashboard exactly like it consumes a fetched catalog.
        /// </summary>
        public JObject ToRow()
        {
            var row = BuildUpdatePayload();
            row["id"] = Id;
            row["achievement_key"] = Key;
            row["bit_index"] = BitIndex;
            return row;
        }

        /// <summary>Overwrites every field from a Supabase row and marks the entry synced.</summary>
        public void ApplyRow(JObject row)
        {
            Id = row.Value<long>("id");
            Key = row.Value<string>("achievement_key") ?? "";
            BitIndex = row.Value<int>("bit_index");
            Title = row.Value<string>("title") ?? "";
            Description = row.Value<string>("description") ?? "";
            IconPath = row.Value<string>("icon_path") ?? "";
            IconUrl = row.Value<string>("icon_url") ?? "";
            Hidden = row.Value<bool?>("hidden") == true;
            Retired = row.Value<bool?>("is_retired") == true;
            DisplayOrder = row.Value<int?>("display_order") ?? 0;
            LocalizationTable = row.Value<string>("localization_table") ?? "";
            TitleKey = row.Value<string>("title_key") ?? "";
            DescriptionKey = row.Value<string>("description_key") ?? "";
            MarkSynced();
        }

        public static DashboardAchievement FromRow(JObject row)
        {
            var achievement = new DashboardAchievement();
            achievement.ApplyRow(row);
            return achievement;
        }

        private static string NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
    }

    internal enum DashboardBindingSource
    {
        /// <summary>A UnityEvent (a Button's onClick, a Toggle, or an event field of one of your scripts) calls a trigger.</summary>
        UnityEvent,

        /// <summary>A bool method/property/field of one of your scripts is watched and fires when it turns true.</summary>
        Condition,
    }

    /// <summary>
    /// One scene hookup of a rule: where the trigger lives. The scene itself holds the truth (an
    /// <c>AchievementTrigger</c>/<c>AchievementMethodWatcher</c> carrying this record's <see cref="Id"/>); this record is
    /// what the dashboard shows and uses to find and remove it again.
    /// </summary>
    internal sealed class DashboardBinding
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>"trigger" for Unlock/Counter rules; "start", "fail" or "complete" for Run rules.</summary>
        public string Role { get; set; } = "trigger";

        [JsonConverter(typeof(StringEnumConverter))]
        public DashboardBindingSource Source { get; set; }

        public string ScenePath { get; set; } = "";
        public string ObjectPath { get; set; } = "";
        public string ComponentType { get; set; } = "";

        /// <summary>The UnityEvent field/property, or the bool member.</summary>
        public string Member { get; set; } = "";

        /// <summary>How much each trigger counts for (counter rules).</summary>
        public int Amount { get; set; } = 1;

        public string Describe() => ObjectPath + " . " + ComponentType + " . " + Member;

        public bool SameHookup(DashboardBinding other) =>
            other != null && Role == other.Role && Source == other.Source && ScenePath == other.ScenePath &&
            ObjectPath == other.ObjectPath && ComponentType == other.ComponentType && Member == other.Member;
    }

    /// <summary>One authored rule: an achievement, how it unlocks, and the scene hookups that drive it.</summary>
    internal sealed class DashboardRule
    {
        public string Id { get; set; } = "";
        public string AchievementKey { get; set; } = "";

        [JsonConverter(typeof(StringEnumConverter))]
        public AchievementRuleKind Kind { get; set; } = AchievementRuleKind.Unlock;

        /// <summary>Counter rules: the number of triggers that unlocks the achievement.</summary>
        public int Target { get; set; } = 5;

        public List<DashboardBinding> Bindings { get; set; } = new List<DashboardBinding>();

        [JsonIgnore] public bool Expanded { get; set; }

        [JsonIgnore] public IReadOnlyList<string> Roles => AchievementRuleDefinition.RolesFor(Kind);

        public string EventName(string role = null) => AchievementRuleDefinition.EventNameFor(Id, Kind, role);

        /// <summary>
        /// Unlock and Counter rules listen to the same event, so switching between them keeps every binding valid;
        /// a Run rule has three events, so its bindings would have to be redone.
        /// </summary>
        public bool CanChangeKindTo(AchievementRuleKind kind) =>
            Bindings.Count == 0 || AchievementRuleDefinition.RolesFor(kind).SequenceEqual(Roles);
    }

    /// <summary>The dashboard's saved state: one game's working copy of its catalog.</summary>
    internal sealed class DashboardData
    {
        public const int CurrentFormatVersion = 1;
        public const string DefaultManifestPath = "Assets/Resources/Achievements/achievements.json";

        /// <summary>
        /// Default folder for icons, relative to the runtime's icon prefix (<c>Achievements/</c> in the bundled
        /// bootstraps): an icon for "first_blood" lives at <c>Assets/Resources/Achievements/images/first_blood.png</c>
        /// and its <c>icon_path</c> is <c>images/first_blood</c>.
        /// </summary>
        public const string DefaultIconFolder = "images";

        /// <summary>Default Unity Localization string table (and asset folder name) for achievement text.</summary>
        public const string DefaultLocalizationTableName = "ST_Achievements";

        /// <summary>Folder the string table collections are created in (one subfolder per table).</summary>
        public const string DefaultLocalizationFolder = "Assets/Localization/Tables";

        public int FormatVersion { get; set; } = CurrentFormatVersion;
        public string GameSlug { get; set; } = "";
        public string GameName { get; set; } = "";

        /// <summary>Server <c>games.id</c>; 0 until the game has been looked up or created.</summary>
        public long GameId { get; set; }

        /// <summary>Server <c>games.catalog_version</c> as of the last pull/push (the manifest carries it).</summary>
        public int CatalogVersion { get; set; }

        public string ManifestPath { get; set; } = DefaultManifestPath;

        public const string DefaultRulesPath = "Assets/Resources/Achievements/rules.json";

        /// <summary>Where the Rules tab writes <c>rules.json</c>, which the game loads from Resources automatically.</summary>
        public string RulesPath { get; set; } = DefaultRulesPath;

        public List<DashboardRule> Rules { get; set; } = new List<DashboardRule>();

        /// <summary>Folder that new achievements' <c>icon_path</c> is filled with (folder + "/" + key).</summary>
        public string IconFolder { get; set; } = DefaultIconFolder;

        /// <summary>
        /// Combined: each achievement's icon already contains its background (default). Layered: one shared
        /// background for every achievement plus each achievement's own icon on top.
        /// </summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public AchievementIconStyle IconStyle { get; set; } = AchievementIconStyle.Combined;

        /// <summary>Layered style: the shared background's icon path; empty means <c>&lt;IconFolder&gt;/background</c>.</summary>
        public string IconBackground { get; set; } = "";

        /// <summary>Layered style: margin around the icon inside the background, as a fraction of its size.</summary>
        public float IconInset { get; set; } = AchievementCatalog.DefaultIconInset;

        [JsonIgnore]
        public string EffectiveIconBackground =>
            string.IsNullOrWhiteSpace(IconBackground) ? IconPathFor("background") : IconBackground.Trim();

        /// <summary>
        /// String table used by every achievement that does not name its own table. Also the name of the
        /// string table collection created by the Localize button.
        /// </summary>
        public string DefaultLocalizationTable { get; set; } = DefaultLocalizationTableName;

        /// <summary>Asset folder the Localize button creates string table collections under.</summary>
        public string LocalizationFolder { get; set; } = DefaultLocalizationFolder;
        public List<DashboardAchievement> Achievements { get; set; } = new List<DashboardAchievement>();

        public static DashboardData Load(string fullPath)
        {
            if (!File.Exists(fullPath)) return new DashboardData();
            var data = JsonConvert.DeserializeObject<DashboardData>(File.ReadAllText(fullPath, Encoding.UTF8));
            if (data == null) return new DashboardData();
            if (data.FormatVersion > CurrentFormatVersion)
                throw new InvalidDataException(
                    "The dashboard file uses format " + data.FormatVersion + ", newer than the supported " + CurrentFormatVersion + ".");
            data.Achievements = data.Achievements ?? new List<DashboardAchievement>();
            data.Rules = data.Rules ?? new List<DashboardRule>();
            foreach (var rule in data.Rules) rule.Bindings = rule.Bindings ?? new List<DashboardBinding>();
            return data;
        }

        /// <summary>Writes atomically (temp file + replace) so a crash mid-save never truncates the saved catalog.</summary>
        public void Save(string fullPath)
        {
            string json = JsonConvert.SerializeObject(this, Formatting.Indented) + "\n";
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            string temp = fullPath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(fullPath)) File.Replace(temp, fullPath, null);
            else File.Move(temp, fullPath);
        }

        [JsonIgnore]
        public IEnumerable<DashboardAchievement> Pending => Achievements.Where(a => a.State != DashboardSyncState.Synced);

        /// <summary>Next unused bit index. Retired entries count too: a bit index is never reused.</summary>
        public int NextBitIndex() => Achievements.Count == 0 ? 0 : Achievements.Max(a => a.BitIndex) + 1;

        /// <summary>The icon path an achievement with this key gets by default: folder + "/" + key.</summary>
        public string IconPathFor(string key)
        {
            string folder = (IconFolder ?? "").Trim().Replace('\\', '/').Trim('/');
            return folder.Length == 0 ? key : folder + "/" + key;
        }

        /// <summary>
        /// Renames a key. While the icon path is still the auto-generated one it follows the key, so typing a
        /// key does not leave a stale "images/new_achievement"; a path the user edited by hand is left alone.
        /// </summary>
        public void RenameKey(DashboardAchievement achievement, string newKey)
        {
            if (achievement.Key == newKey) return;
            if (achievement.IconPath == IconPathFor(achievement.Key)) achievement.IconPath = IconPathFor(newKey);
            if (achievement.TitleKey == DefaultTitleKey(achievement.Key)) achievement.TitleKey = DefaultTitleKey(newKey);
            if (achievement.DescriptionKey == DefaultDescriptionKey(achievement.Key)) achievement.DescriptionKey = DefaultDescriptionKey(newKey);
            foreach (var rule in Rules.Where(r => r.AchievementKey == achievement.Key)) rule.AchievementKey = newKey; // rules follow the rename
            achievement.Key = newKey;
        }

        // ---- rules ------------------------------------------------------------------------------------------

        /// <summary>Adds a rule with a fresh, permanent id (its event names derive from it and never change).</summary>
        public DashboardRule AddRule(string achievementKey = "")
        {
            var taken = new HashSet<string>(Rules.Select(r => r.Id), StringComparer.Ordinal);
            string id;
            do id = "r" + Guid.NewGuid().ToString("N").Substring(0, 6); while (!taken.Add(id));

            var rule = new DashboardRule { Id = id, AchievementKey = achievementKey ?? "", Expanded = true };
            Rules.Add(rule);
            return rule;
        }

        /// <summary>Problems that stop a rule from being written to rules.json.</summary>
        public List<string> ValidateRule(DashboardRule rule)
        {
            var errors = new List<string>();
            var achievement = Achievements.FirstOrDefault(a => a.Key == rule.AchievementKey);

            if (string.IsNullOrEmpty(rule.AchievementKey)) errors.Add("Choose the achievement this rule unlocks.");
            else if (achievement == null) errors.Add("The achievement '" + rule.AchievementKey + "' is not in the dashboard.");
            else if (achievement.Retired) errors.Add("'" + rule.AchievementKey + "' is retired and can no longer be unlocked.");

            if (rule.Kind == AchievementRuleKind.Counter)
            {
                if (rule.Target < 1) errors.Add("A counter's target must be at least 1.");
                if (Rules.Any(o => !ReferenceEquals(o, rule) && o.Kind == AchievementRuleKind.Counter && o.AchievementKey == rule.AchievementKey && !string.IsNullOrEmpty(rule.AchievementKey)))
                    errors.Add("Another counter rule already counts for '" + rule.AchievementKey + "'; two would share one saved value.");
            }
            return errors;
        }

        /// <summary>Builds the rule set the game runs from every valid rule; the invalid ones are reported in <paramref name="skipped"/>.</summary>
        public AchievementRuleSet BuildRuleSet(out List<string> skipped)
        {
            skipped = new List<string>();
            var definitions = new List<AchievementRuleDefinition>();
            foreach (var rule in Rules)
            {
                var errors = ValidateRule(rule);
                if (errors.Count > 0)
                {
                    skipped.Add(string.IsNullOrEmpty(rule.AchievementKey) ? rule.Id : rule.AchievementKey + ": " + errors[0]);
                    continue;
                }
                definitions.Add(new AchievementRuleDefinition(rule.Id, rule.AchievementKey, rule.Kind, rule.Target));
            }
            return new AchievementRuleSet(definitions);
        }

        public string BuildRulesJson(out List<string> skipped) => BuildRuleSet(out skipped).ToJson();

        // ---- localization ---------------------------------------------------------------------------------

        public static string DefaultTitleKey(string key) => key + "_title";

        public static string DefaultDescriptionKey(string key) => key + "_description";

        /// <summary>The table an achievement's text lives in: its own override, else <see cref="DefaultLocalizationTable"/>.</summary>
        public string EffectiveTable(DashboardAchievement a) =>
            !string.IsNullOrWhiteSpace(a.LocalizationTable) ? a.LocalizationTable.Trim() : (DefaultLocalizationTable ?? "").Trim();

        public static string EffectiveTitleKey(DashboardAchievement a) =>
            !string.IsNullOrWhiteSpace(a.TitleKey) ? a.TitleKey.Trim() : DefaultTitleKey(a.Key);

        public static string EffectiveDescriptionKey(DashboardAchievement a) =>
            !string.IsNullOrWhiteSpace(a.DescriptionKey) ? a.DescriptionKey.Trim() : DefaultDescriptionKey(a.Key);

        /// <summary>
        /// Every (table, key, text) the string tables need: for each achievement its title and description
        /// under the keys it overrides, or <c>key_title</c> / <c>key_description</c> when it does not. The
        /// text is the achievement's current title/description, the source-language starting point for
        /// translators. A (table, key) pair is listed once; the first achievement to claim it wins.
        /// </summary>
        public List<LocalizationEntryPlan> BuildLocalizationPlan()
        {
            var plan = new List<LocalizationEntryPlan>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string table, string key, string text)
            {
                if (seen.Add(table + "\n" + key)) plan.Add(new LocalizationEntryPlan(table, key, text ?? ""));
            }

            foreach (var a in Achievements.Where(a => !string.IsNullOrEmpty(a.Key)).OrderBy(a => a.BitIndex))
            {
                string table = EffectiveTable(a);
                Add(table, EffectiveTitleKey(a), a.Title);
                Add(table, EffectiveDescriptionKey(a), a.Description);
            }
            return plan;
        }

        /// <summary>
        /// Writes the effective table and keys into every achievement's empty localization fields, so the
        /// database row (and the manifest) reference the entries the string table now holds. Fields that were
        /// filled in by hand are overrides and stay as they are.
        /// </summary>
        /// <returns>How many achievements changed.</returns>
        public int ApplyLocalizationDefaults()
        {
            int changed = 0;
            foreach (var a in Achievements.Where(a => !string.IsNullOrEmpty(a.Key)))
            {
                string table = EffectiveTable(a);
                string titleKey = EffectiveTitleKey(a);
                string descriptionKey = EffectiveDescriptionKey(a);
                bool touched = a.LocalizationTable != table || a.TitleKey != titleKey || a.DescriptionKey != descriptionKey;
                a.LocalizationTable = table;
                a.TitleKey = titleKey;
                a.DescriptionKey = descriptionKey;
                if (touched) changed++;
            }
            return changed;
        }

        /// <summary>
        /// Changes the icon folder and moves the auto-generated icon paths of never-pushed entries with it.
        /// Pushed entries keep their path: it is already stored on the server (and may be shipped).
        /// </summary>
        public void SetIconFolder(string newFolder)
        {
            var followers = Achievements
                .Where(a => a.State == DashboardSyncState.New && a.IconPath == IconPathFor(a.Key))
                .ToList();
            IconFolder = newFolder;
            foreach (var a in followers) a.IconPath = IconPathFor(a.Key);
        }

        public DashboardAchievement AddNew()
        {
            string key = UniqueKey("new_achievement");
            var achievement = new DashboardAchievement
            {
                Key = key,
                Title = "New Achievement",
                BitIndex = NextBitIndex(),
                IconPath = IconPathFor(key),
                Expanded = true,
            };
            Achievements.Add(achievement);
            return achievement;
        }

        private string UniqueKey(string baseKey)
        {
            var taken = new HashSet<string>(Achievements.Select(a => a.Key), StringComparer.Ordinal);
            if (!taken.Contains(baseKey)) return baseKey;
            for (int suffix = 2; ; suffix++)
            {
                string candidate = baseKey + "_" + suffix;
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        /// <summary>
        /// Merges rows fetched from Supabase into the working copy. Untouched entries take the server's
        /// values; entries with unsent local edits are kept as they are (their edits win until pushed) and
        /// counted, and rows the working copy has never seen are added.
        /// </summary>
        public PullResult MergeRemote(JArray rows)
        {
            var result = new PullResult();
            foreach (var token in rows)
            {
                var row = (JObject)token;
                long id = row.Value<long>("id");
                var local = Achievements.FirstOrDefault(a => a.Id == id);

                if (local == null)
                {
                    Achievements.Add(DashboardAchievement.FromRow(row));
                    result.Added++;
                }
                else if (local.State == DashboardSyncState.Modified)
                {
                    result.LocalEditsKept++;
                }
                else
                {
                    bool changed = !JToken.DeepEquals(local.ToRow(), DashboardAchievement.FromRow(row).ToRow());
                    local.ApplyRow(row);
                    if (changed) result.Updated++;
                }
            }
            return result;
        }

        /// <summary>
        /// Builds the manifest the game ships from every entry that already has a server id (the runtime
        /// needs <c>id</c> to sync unlocks, so a never-pushed entry cannot be in a manifest).
        /// </summary>
        public JObject BuildManifest()
        {
            var rows = new JArray(Achievements.Where(a => a.Id > 0).Select(a => a.ToRow()));
            var icon = BuildGameIconPayload();
            return AchievementManifestBuilder.Build(GameId, GameSlug, CatalogVersion, rows,
                icon.Value<string>("icon_style"), icon.Value<string>("icon_background"), icon.Value<double?>("icon_inset"));
        }

        // ---- game-wide icon style (games.icon_style / icon_background / icon_inset) -----------------------

        /// <summary>Serialized icon settings as of the last push/pull; null until the game row has been read or written.</summary>
        public string GameIconSnapshot { get; set; }

        /// <summary>What a PATCH of the game row carries: the style, and the background/inset only while layered.</summary>
        public JObject BuildGameIconPayload()
        {
            bool layered = IconStyle == AchievementIconStyle.Layered;
            return new JObject
            {
                ["icon_style"] = layered ? "layered" : "combined",
                ["icon_background"] = layered ? StripExtension(EffectiveIconBackground) : null,
                ["icon_inset"] = layered ? (JToken)Math.Round(Math.Min(0.45, Math.Max(0, IconInset)), 3) : JValue.CreateNull(),
            };
        }

        // A game that was never written to has the database default (combined, nothing else).
        private static string DefaultGameIconSnapshot() =>
            new DashboardData { IconStyle = AchievementIconStyle.Combined }.BuildGameIconPayload().ToString(Formatting.None);

        /// <summary>True when the local icon settings differ from what Supabase holds, so a push has something to send.</summary>
        [JsonIgnore]
        public bool IsGameIconPending =>
            GameId > 0 && BuildGameIconPayload().ToString(Formatting.None) != (GameIconSnapshot ?? DefaultGameIconSnapshot());

        public void MarkGameIconSynced() => GameIconSnapshot = BuildGameIconPayload().ToString(Formatting.None);

        /// <summary>
        /// Takes the icon settings from a fetched game row. Unsent local edits are kept (returns false), and a
        /// row without the icon columns - a database that has not applied the migration - changes nothing.
        /// </summary>
        public bool ApplyGameIcon(JObject game)
        {
            if (!game.ContainsKey("icon_style")) return true;
            if (IsGameIconPending) return false;

            IconStyle = string.Equals(game.Value<string>("icon_style"), "layered", StringComparison.OrdinalIgnoreCase)
                ? AchievementIconStyle.Layered : AchievementIconStyle.Combined;
            string background = game.Value<string>("icon_background");
            if (!string.IsNullOrEmpty(background)) IconBackground = background;
            double? inset = game.Value<double?>("icon_inset");
            if (inset.HasValue) IconInset = (float)Math.Round(inset.Value, 3);
            MarkGameIconSynced();
            return true;
        }

        private static string StripExtension(string path)
        {
            int dot = path.LastIndexOf('.');
            int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            return dot > slash ? path.Substring(0, dot) : path;
        }

        public static string SerializeManifest(JObject manifest) => JsonConvert.SerializeObject(manifest, Formatting.Indented) + "\n";
    }

    internal struct PullResult
    {
        public int Added;
        public int Updated;
        public int LocalEditsKept;
    }

    /// <summary>Mirrors the database CHECK constraints so problems show up before a round trip fails.</summary>
    internal static class DashboardValidator
    {
        // achievements.achievement_key check, verbatim from the base migration.
        private static readonly Regex KeyPattern = new Regex("^[a-zA-Z0-9]([a-zA-Z0-9_.-]{0,62}[a-zA-Z0-9])?$", RegexOptions.Compiled);

        // games.slug check, verbatim from the base migration.
        private static readonly Regex SlugPattern = new Regex("^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.Compiled);

        public static List<string> Validate(DashboardAchievement a, IReadOnlyCollection<DashboardAchievement> all)
        {
            var errors = new List<string>();

            if (string.IsNullOrEmpty(a.Key) || !KeyPattern.IsMatch(a.Key))
                errors.Add("Key must be 1-64 characters: letters, digits, '_', '.', '-' (no leading/trailing symbol).");
            else if (all.Any(o => !ReferenceEquals(o, a) && o.Key == a.Key))
                errors.Add("Another achievement already uses the key '" + a.Key + "'.");

            if (a.BitIndex < 0 || a.BitIndex > AchievementCatalog.MaxBitIndex)
                errors.Add("Bit index must be within 0.." + AchievementCatalog.MaxBitIndex + ".");
            else if (all.Any(o => !ReferenceEquals(o, a) && o.BitIndex == a.BitIndex))
                errors.Add("Bit index " + a.BitIndex + " is already taken.");

            if (string.IsNullOrEmpty(a.Title) || a.Title.Length > 200) errors.Add("Title must be 1-200 characters.");
            if (a.Description != null && a.Description.Length > 1000) errors.Add("Description can be at most 1000 characters.");
            if (!string.IsNullOrEmpty(a.IconPath) && a.IconPath.Length > 500) errors.Add("Icon path can be at most 500 characters.");

            if (!string.IsNullOrEmpty(a.IconUrl))
            {
                if (a.IconUrl.Length > 2000) errors.Add("Icon URL can be at most 2000 characters.");
                if (!HasWebPrefix(a.IconUrl)) errors.Add("Icon URL must start with http://, https:// or www.");
            }

            if (a.DisplayOrder < short.MinValue || a.DisplayOrder > short.MaxValue)
                errors.Add("Display order must fit a smallint (" + short.MinValue + ".." + short.MaxValue + ").");

            foreach (var (label, value) in new[] { ("Localization table", a.LocalizationTable), ("Title key", a.TitleKey), ("Description key", a.DescriptionKey) })
                if (!string.IsNullOrEmpty(value) && value.Length > 200) errors.Add(label + " can be at most 200 characters.");

            if ((!string.IsNullOrEmpty(a.TitleKey) || !string.IsNullOrEmpty(a.DescriptionKey)) && string.IsNullOrEmpty(a.LocalizationTable))
                errors.Add("Title/description keys need a localization table.");

            return errors;
        }

        public static string ValidateGame(string slug, string name)
        {
            if (string.IsNullOrEmpty(slug) || !SlugPattern.IsMatch(slug))
                return "Game slug must be lowercase letters, digits and '-' (1-64 characters, no leading/trailing '-').";
            if (!string.IsNullOrEmpty(name) && name.Length > 200) return "Game name can be at most 200 characters.";
            return null;
        }

        /// <summary>Checks the names the Localize button hands to Unity Localization; null when they are usable.</summary>
        public static string ValidateLocalizationSetup(string table, string folder)
        {
            if (string.IsNullOrWhiteSpace(table)) return "Enter a default localization table name (e.g. ST_Achievements).";
            if (table.Trim().Length > 200) return "The localization table name can be at most 200 characters.";
            if (table.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || table.Contains("[") || table.Contains("]"))
                return "The localization table name cannot contain invalid file name characters or [ ].";

            string normalized = (folder ?? "").Trim().Replace('\\', '/').Trim('/');
            if (normalized != "Assets" && !normalized.StartsWith("Assets/", StringComparison.Ordinal))
                return "The localization folder must be inside the project's Assets folder (e.g. Assets/Localization/Tables).";
            if (normalized.Split('/').Any(part => part == ".." || part == "." || part.Length == 0))
                return "The localization folder must not contain empty, '.' or '..' segments.";
            return null;
        }

        public static bool HasWebPrefix(string value) =>
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }
}
