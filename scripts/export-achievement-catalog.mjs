#!/usr/bin/env node
// Exports one game's achievement catalog from Supabase into the read-only manifest the game ships.
//
//   node scripts/export-achievement-catalog.mjs <game-slug> <output.json>
//
// Env (read from the process or the repo-root .env):
//   SUPABASE_URL              defaults to the launcher's project URL
//   SUPABASE_PUBLISHABLE_KEY  public sb_publishable_... key; enough for active games (catalog RLS
//                             allows anon SELECT). SUPABASE_PUBLISHABLE_KEYS is accepted as an alias
//                             (plain value or Supabase's {"default": "..."} JSON form).
//   SUPABASE_EXPORT_KEY       optional; only needed to export an unreleased (is_active = false) game.
//                             Use a service/secret key from your own machine only. Never commit it,
//                             never put it in a game build or the launcher.
//
// Output is deterministic (sorted by bit index, fixed key order, 2-space JSON, trailing newline), so
// re-exporting an unchanged catalog produces a byte-identical file and a clean git diff.

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const DEFAULT_SUPABASE_URL = 'https://opvpbtcxxwagodhyzopx.supabase.co';
const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');

function loadDotEnv() {
  try {
    for (const line of readFileSync(resolve(repoRoot, '.env'), 'utf8').split(/\r?\n/)) {
      const match = /^\s*([A-Z0-9_]+)\s*=\s*(.*)\s*$/.exec(line);
      if (match && !(match[1] in process.env)) process.env[match[1]] = match[2].replace(/^['"]|['"]$/g, '');
    }
  } catch {
    // No .env: rely on the process environment.
  }
}

function readPublishableKey() {
  const single = process.env.SUPABASE_PUBLISHABLE_KEY;
  if (single) return single;
  const multi = process.env.SUPABASE_PUBLISHABLE_KEYS;
  if (!multi) return undefined;
  if (!multi.trim().startsWith('{')) return multi;
  try {
    const keys = JSON.parse(multi);
    return keys.default ?? Object.values(keys)[0];
  } catch {
    return undefined;
  }
}

async function get(baseUrl, key, path) {
  // sb_publishable_/sb_secret_ keys are not JWTs: they belong in the apikey header only.
  const headers = { apikey: key, Accept: 'application/json' };
  if (key.startsWith('eyJ')) headers.Authorization = `Bearer ${key}`;
  const response = await fetch(`${baseUrl}/rest/v1/${path}`, { headers });
  if (!response.ok) throw new Error(`GET ${path} -> ${response.status} ${await response.text()}`);
  return response.json();
}

export function buildManifest(game, rows) {
  const achievements = [...rows]
    .sort((a, b) => a.bit_index - b.bit_index)
    .map((row) => {
      const entry = {
        id: row.id,
        key: row.achievement_key,
        bitIndex: row.bit_index,
        title: row.title,
        description: row.description,
      };
      if (row.icon_path) {
        // A full URL (http/https/www) is downloaded at runtime instead of loaded from packaged
        // Resources, so its extension must be kept; only a local Resources-relative path has its
        // extension stripped (Resources.Load takes no extension).
        entry.icon = /^(https?:\/\/|www\.)/i.test(row.icon_path) ? row.icon_path : row.icon_path.replace(/\.[a-z0-9]+$/i, '');
      }
      if (row.hidden) entry.hidden = true;
      if (row.is_retired) entry.retired = true;
      if (row.display_order) entry.displayOrder = row.display_order;
      if (row.localization_table) {
        entry.localization = { table: row.localization_table };
        if (row.title_key) entry.localization.titleKey = row.title_key;
        if (row.description_key) entry.localization.descriptionKey = row.description_key;
      }
      return entry;
    });

  const seenBits = new Set();
  for (const entry of achievements) {
    if (seenBits.has(entry.bitIndex)) throw new Error(`Duplicate bit index ${entry.bitIndex}`);
    seenBits.add(entry.bitIndex);
  }

  return {
    formatVersion: 1,
    game: game.slug,
    gameId: game.id,
    catalogVersion: game.catalog_version,
    achievements,
  };
}

async function main() {
  const [slug, output] = process.argv.slice(2);
  if (!slug || !output) {
    console.error('Usage: node scripts/export-achievement-catalog.mjs <game-slug> <output.json>');
    process.exit(2);
  }

  loadDotEnv();
  const baseUrl = (process.env.SUPABASE_URL || DEFAULT_SUPABASE_URL).replace(/\/$/, '');
  const key = process.env.SUPABASE_EXPORT_KEY || readPublishableKey();
  if (!key) throw new Error('Add SUPABASE_PUBLISHABLE_KEY=sb_publishable_... to .env (or set SUPABASE_EXPORT_KEY for unreleased games).');

  const games = await get(baseUrl, key, `games?slug=eq.${encodeURIComponent(slug)}&select=id,slug,catalog_version`);
  if (games.length !== 1) throw new Error(`Game '${slug}' not found (inactive games need SUPABASE_EXPORT_KEY).`);

  const rows = await get(
    baseUrl,
    key,
    `achievements?game_id=eq.${games[0].id}&select=id,achievement_key,bit_index,title,description,icon_path,hidden,is_retired,display_order,localization_table,title_key,description_key&order=bit_index.asc`,
  );

  const manifest = buildManifest(games[0], rows);
  const target = resolve(process.cwd(), output);
  mkdirSync(dirname(target), { recursive: true });
  writeFileSync(target, `${JSON.stringify(manifest, null, 2)}\n`);
  console.log(`Wrote ${manifest.achievements.length} achievements for '${slug}' (catalog v${manifest.catalogVersion}) to ${target}`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(error.message);
    process.exit(1);
  });
}
