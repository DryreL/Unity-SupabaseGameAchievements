-- =============================================================================
-- Game-wide icon style: how the unlock toast composes an achievement's icon.
-- =============================================================================
--
--   combined  each achievement's image already contains its own background (the default)
--   layered   one shared background image for the whole game, with each achievement's own icon on top
--
-- This is a property of the game, not of a single achievement, so it lives on public.games. It is
-- copied into the manifest (iconStyle / iconBackground / iconInset) by every exporter, so the Editor
-- dashboard, the Editor exporter window and scripts/export-achievement-catalog.mjs all produce the
-- same file. icon_background is a Resources-relative path without extension, like achievements.icon_path.
--
-- Changing any of the three columns bumps games.catalog_version, exactly like a catalog change does,
-- so a Remote Config manifest with the new style outranks the bundled one.

alter table public.games
  add column if not exists icon_style      text not null default 'combined',
  add column if not exists icon_background text,
  add column if not exists icon_inset      real;

alter table public.games drop constraint if exists games_icon_style_valid;
alter table public.games add constraint games_icon_style_valid
  check (icon_style in ('combined', 'layered'));

alter table public.games drop constraint if exists games_icon_background_length;
alter table public.games add constraint games_icon_background_length
  check (icon_background is null or char_length(icon_background) between 1 and 500);

-- Same bounds the runtime clamps to (AchievementCatalog.IconInset): 0 .. 0.45 of the background size.
alter table public.games drop constraint if exists games_icon_inset_range;
alter table public.games add constraint games_icon_inset_range
  check (icon_inset is null or (icon_inset >= 0 and icon_inset <= 0.45));

comment on column public.games.icon_style is
  'combined = one image per achievement including its background (default); layered = shared background plus each achievement''s icon.';
comment on column public.games.icon_background is
  'Layered style: Resources-relative path (no extension) of the shared background image. NULL = the client default.';
comment on column public.games.icon_inset is
  'Layered style: margin around the icon inside the background as a fraction of its size (0..0.45). NULL = the client default.';

create or replace function private.bump_catalog_version_on_game_icon_change()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
  -- Only when the icon presentation actually changed, and only if the writer did not already move the
  -- version itself (an importer that sets catalog_version explicitly keeps what it wrote).
  if (old.icon_style, old.icon_background, old.icon_inset) is distinct from (new.icon_style, new.icon_background, new.icon_inset)
     and new.catalog_version = old.catalog_version then
    new.catalog_version := old.catalog_version + 1;
  end if;
  return new;
end;
$$;

revoke all on function private.bump_catalog_version_on_game_icon_change() from public;

drop trigger if exists games_bump_catalog_version_on_icon_change on public.games;
create trigger games_bump_catalog_version_on_icon_change
  before update on public.games
  for each row execute function private.bump_catalog_version_on_game_icon_change();

-- No grant changes: games keeps its table-level SELECT for anon/authenticated (active games only, via RLS)
-- and no client write grants; the new columns inherit both. Writes come from the service role only.
