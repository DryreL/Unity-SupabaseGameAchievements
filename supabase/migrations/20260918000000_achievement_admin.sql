-- Administrative achievement management for the Editor tooling
-- (Tools > DryreL Hub > Supabase Game Achievements > Manage Achievements).
--
-- Both functions are service_role-only: no client (anon or authenticated) can retire or delete an
-- achievement. They exist so an Editor tool can do these operations through PostgREST with a
-- service/secret key instead of requiring the SQL editor for routine catalog maintenance.

-- ---------------------------------------------------------------------------
-- retire_achievement: the safe, normal way to remove an achievement from play.
-- Equivalent to `update achievements set is_retired = true where id = ...`, exposed as an RPC so the
-- Editor tool never needs write access to the table itself (see the empty grants in the base migration).
-- ---------------------------------------------------------------------------

create function public.retire_achievement(p_achievement_id bigint)
returns public.achievements
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_row public.achievements;
begin
  update public.achievements
     set is_retired = true
   where id = p_achievement_id
  returning * into v_row;

  if not found then
    raise exception 'achievement % not found', p_achievement_id using errcode = 'P0001';
  end if;

  return v_row;
end;
$$;

comment on function public.retire_achievement(bigint) is
  'Marks an achievement retired (is_retired = true). service_role only. Idempotent: retiring an already-retired achievement is a no-op that returns its current row.';

revoke all on function public.retire_achievement(bigint) from public, anon, authenticated;
grant execute on function public.retire_achievement(bigint) to service_role;

-- ---------------------------------------------------------------------------
-- delete_retired_achievement: hard delete, deliberately narrow.
--
-- The base migration's achievements_guard_identity trigger blocks every DELETE unless the
-- achievements.allow_hard_delete session setting is 'on' for that transaction, specifically so a
-- bit_index can never be freed and reused by accident. This function is the one sanctioned way past
-- that guard, and only for a row that:
--   1. Is already retired (never delete a live achievement out from under a shipped client).
--   2. Has no unlock rows in user_achievements (never delete an achievement anyone has actually earned;
--      that data belongs to players and must survive catalog cleanup).
-- Deleting a genuinely never-shipped/never-earned mistake this way is safe: its bit_index is freed
-- correctly (no achievement ever referenced it in a released build), unlike retiring, which burns the
-- bit_index forever on purpose.
-- ---------------------------------------------------------------------------

create function public.delete_retired_achievement(p_achievement_id bigint)
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

  select count(*) into v_unlock_count from public.user_achievements where achievement_id = p_achievement_id;
  if v_unlock_count > 0 then
    raise exception 'achievement % (%) has % player unlock(s) and cannot be deleted; leave it retired', p_achievement_id, v_key, v_unlock_count
      using errcode = 'P0001';
  end if;

  set local achievements.allow_hard_delete = 'on';
  delete from public.achievements where id = p_achievement_id;
end;
$$;

comment on function public.delete_retired_achievement(bigint) is
  'Permanently deletes an achievement. service_role only. Requires it to already be retired and to have zero player unlocks. Frees its bit_index for reuse - only ever call this for a mistake that never shipped, not a real removal (use retire_achievement for that).';

revoke all on function public.delete_retired_achievement(bigint) from public, anon, authenticated;
grant execute on function public.delete_retired_achievement(bigint) to service_role;
