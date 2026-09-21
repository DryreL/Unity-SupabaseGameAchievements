-- =============================================================================
-- Patreon identity on achievements: one row per (Patreon account, game), whatever auth.users id
-- the account happened to be signed in as.
-- =============================================================================
--
-- Problem: user_achievements was keyed only by auth.users.id, and patreon-middleware located that
-- user by a synthetic e-mail (patreon-<id>@<IDENTITY_EMAIL_DOMAIN>). Anything that changes the
-- e-mail (a different IDENTITY_EMAIL_DOMAIN, an account created by hand, ...) mints a second
-- auth user for the same Patreon account, and the player's unlocks split across user_ids.
--
-- Fix:
--   * patreon_identities.patreon_id is UNIQUE and is the source of truth for "which auth user is this
--     Patreon account". The Edge Function now resolves the user through it (see index.ts).
--   * user_achievements gains patreon_id / patreon_username / game_slug (maintained by triggers,
--     never client-supplied), plus a unique (patreon_id, game_id) so a split can not happen again.
--   * Existing duplicates are merged here (union of unlocked ids) into one canonical user per
--     Patreon id: the one that already has a profile, else the oldest auth user.
--
-- Duplicate auth.users rows are left in place (their achievement rows are merged away); delete
-- them by hand once you have checked nothing else references them.

-- -----------------------------------------------------------------------------
-- 1. patreon_identities: which auth user IS a Patreon account
-- -----------------------------------------------------------------------------
-- A dedicated table on purpose: public.user_profiles already exists in the live project (used by
-- patreon-user-info: keyed by "id", holds role/tier columns) and must not be touched here.
--
-- Written ONLY by the patreon-middleware Edge Function (service_role) after Patreon itself verified
-- the token. If clients could write it they could claim someone else's patreon_id and be handed
-- that person's session on their next sign-in.

create table if not exists public.patreon_identities (
  user_id           uuid        primary key references auth.users (id) on delete cascade,
  patreon_id        text        not null unique,
  patreon_username  text,
  updated_at        timestamptz not null default now()
);

alter table public.patreon_identities enable row level security;
alter table public.user_achievements enable row level security;

drop policy if exists "Users can read their own patreon identity" on public.patreon_identities;
create policy "Users can read their own patreon identity"
  on public.patreon_identities
  for select
  to authenticated
  using ((select auth.uid()) = user_id);

revoke all on table public.patreon_identities from anon, authenticated;
grant select on table public.patreon_identities to authenticated;

comment on table public.patreon_identities is
  'Patreon account -> Supabase auth user. Written only by the patreon-middleware Edge Function (service_role). patreon_id is unique: it, not the synthetic e-mail, identifies the account.';

-- -----------------------------------------------------------------------------
-- 2. New denormalized columns
-- -----------------------------------------------------------------------------

alter table public.user_achievements
  add column if not exists patreon_id       text,
  add column if not exists patreon_username text,
  add column if not exists game_slug        text;

comment on column public.user_achievements.patreon_id is
  'Denormalized from patreon_identities by trigger; never client-supplied.';
comment on column public.user_achievements.patreon_username is
  'Denormalized from patreon_identities by trigger; kept current when the profile changes.';
comment on column public.user_achievements.game_slug is
  'Denormalized from games.slug by trigger, so the table is readable without joining games.';

-- -----------------------------------------------------------------------------
-- 3. Canonical user per Patreon id, then merge duplicates into it
-- -----------------------------------------------------------------------------

insert into public.patreon_identities (user_id, patreon_id)
select distinct on (au.raw_app_meta_data ->> 'patreon_id')
       au.id, au.raw_app_meta_data ->> 'patreon_id'
  from auth.users au
 where au.raw_app_meta_data ->> 'patreon_id' is not null
   and not exists (
     select 1 from public.patreon_identities p
      where p.patreon_id = au.raw_app_meta_data ->> 'patreon_id')
 order by au.raw_app_meta_data ->> 'patreon_id', au.created_at, au.id
on conflict (user_id) do update
   set patreon_id = excluded.patreon_id, updated_at = now();

-- One statement (no scratch table): fold every duplicate user's unlocks into the canonical user's
-- row, then delete the duplicates' rows. The only rows removed are user_achievements rows whose
-- ids were just merged into the canonical row; auth.users is never touched.
with dupes as (
  select au.id as dup_user_id, p.user_id as canonical_user_id
    from auth.users au
    join public.patreon_identities p on p.patreon_id = au.raw_app_meta_data ->> 'patreon_id'
   where au.id <> p.user_id
),
merged as (
  insert into public.user_achievements (user_id, game_id, achievement_ids, updated_at)
  select d.canonical_user_id, ua.game_id,
         array_agg(distinct x.id order by x.id),
         max(ua.updated_at)
    from dupes d
    join public.user_achievements ua on ua.user_id = d.dup_user_id
   cross join lateral unnest(ua.achievement_ids) as x(id)
   group by d.canonical_user_id, ua.game_id
  on conflict (user_id, game_id) do update
     set achievement_ids = (
           select coalesce(array_agg(distinct y.id order by y.id), '{}')
             from unnest(public.user_achievements.achievement_ids || excluded.achievement_ids) as y(id)),
         updated_at = greatest(public.user_achievements.updated_at, excluded.updated_at)
  returning 1
)
delete from public.user_achievements ua
 using dupes d
 where ua.user_id = d.dup_user_id;

-- -----------------------------------------------------------------------------
-- 4. Triggers keeping the denormalized columns correct
-- -----------------------------------------------------------------------------

create or replace function private.fill_user_achievement_identity()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
  select g.slug into new.game_slug
    from public.games g
   where g.id = new.game_id;

  select p.patreon_id, p.patreon_username
    into new.patreon_id, new.patreon_username
    from public.patreon_identities p
   where p.user_id = new.user_id;

  -- No profile yet (first sync raced the profile write): fall back to the service-role-only
  -- app_metadata the Edge Function stamped on the auth user.
  if new.patreon_id is null then
    select au.raw_app_meta_data ->> 'patreon_id'
      into new.patreon_id
      from auth.users au
     where au.id = new.user_id;
  end if;

  return new;
end;
$$;

revoke all on function private.fill_user_achievement_identity() from public;

drop trigger if exists user_achievements_fill_identity on public.user_achievements;
create trigger user_achievements_fill_identity
  before insert or update on public.user_achievements
  for each row execute function private.fill_user_achievement_identity();

create or replace function private.propagate_profile_to_achievements()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
  -- Every sign-in upserts the profile; only touch achievement rows when something changed.
  if tg_op = 'UPDATE'
     and old.patreon_id is not distinct from new.patreon_id
     and old.patreon_username is not distinct from new.patreon_username then
    return null;
  end if;

  -- Re-runs fill_user_achievement_identity for this user's rows.
  update public.user_achievements
     set updated_at = updated_at
   where user_id = new.user_id;
  return null;
end;
$$;

revoke all on function private.propagate_profile_to_achievements() from public;

drop trigger if exists patreon_identities_propagate on public.patreon_identities;
create trigger patreon_identities_propagate
  after insert or update of patreon_id, patreon_username on public.patreon_identities
  for each row execute function private.propagate_profile_to_achievements();

-- Backfill every existing row through the trigger.
update public.user_achievements set updated_at = updated_at;

-- One row per Patreon account per game, whatever auth user id it signed in as.
create unique index if not exists user_achievements_patreon_game_key
  on public.user_achievements (patreon_id, game_id)
  where patreon_id is not null;

create index if not exists idx_user_achievements_game_slug
  on public.user_achievements (game_slug);

-- -----------------------------------------------------------------------------
-- 5. Readable view (replaces the earlier definition)
-- -----------------------------------------------------------------------------
-- security_invoker: without it the view runs as its owner and would show every player's rows to
-- any authenticated client, bypassing the RLS on user_achievements.

drop view if exists public.user_achievements_readable;

create view public.user_achievements_readable
with (security_invoker = true) as
select ua.user_id,
       ua.patreon_id,
       ua.patreon_username,
       ua.game_id,
       ua.game_slug,
       a.id              as achievement_id,
       a.achievement_key,
       a.title           as achievement_title,
       ua.updated_at     as unlocked_at
  from public.user_achievements ua
 cross join lateral unnest(ua.achievement_ids) as u(ach_id)
  join public.achievements a on a.id = u.ach_id;

revoke all on public.user_achievements_readable from anon, authenticated;
grant select on public.user_achievements_readable to authenticated;

-- -----------------------------------------------------------------------------
-- 6. Admin fix: user_achievements stores an id array, not one row per unlock
-- -----------------------------------------------------------------------------
-- 20260918000000_achievement_admin.sql counted unlocks with `where achievement_id = ...`, a column
-- that no longer exists, so delete_retired_achievement always failed.

create or replace function public.delete_retired_achievement(p_achievement_id bigint)
returns void
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_is_retired boolean;
  v_key text;
  v_unlock_count bigint;
begin
  select is_retired, achievement_key into v_is_retired, v_key
    from public.achievements
   where id = p_achievement_id;

  if not found then
    raise exception 'achievement % not found', p_achievement_id using errcode = 'P0001';
  end if;

  if not v_is_retired then
    raise exception 'achievement % (%) must be retired before it can be deleted; call retire_achievement first', p_achievement_id, v_key
      using errcode = 'P0001';
  end if;

  select count(*) into v_unlock_count
    from public.user_achievements
   where achievement_ids @> array[p_achievement_id];
  if v_unlock_count > 0 then
    raise exception 'achievement % (%) has % player unlock(s) and cannot be deleted; leave it retired', p_achievement_id, v_key, v_unlock_count
      using errcode = 'P0001';
  end if;

  set local achievements.allow_hard_delete = 'on';
  delete from public.achievements where id = p_achievement_id;
end;
$$;

revoke all on function public.delete_retired_achievement(bigint) from public, anon, authenticated;
grant execute on function public.delete_retired_achievement(bigint) to service_role;
