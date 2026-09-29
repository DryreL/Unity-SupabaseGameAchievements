-- Per-user rate limit on sync_achievements: at most 60 calls per rolling-reset minute.
--
-- Legitimate play never gets near this (the client debounces 1 s, batches up to 100 ids per call and
-- drains an offline backlog in one request). A call over the limit answers HTTP 429 (SQLSTATE PT429,
-- PostgREST maps PTxxx to the status), which the client treats as transient: it keeps the unlocks
-- pending and retries with backoff, so a burst is delayed, never lost. The counter rolls back with the
-- rejected call, so blocked calls cost one row lock and nothing else.

create table if not exists private.sync_rate_limits (
  user_id      uuid primary key references auth.users (id) on delete cascade,
  window_start timestamptz not null default now(),
  calls        integer     not null default 0
);
revoke all on private.sync_rate_limits from public, anon, authenticated;

create or replace function private.sync_achievements(p_game_id bigint, p_achievement_ids bigint[])
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  c_max_calls   constant integer := 60;
  c_window      constant interval := interval '1 minute';
  v_user_id     uuid := auth.uid();
  v_calls       integer;
  v_valid_ids   bigint[];
  v_rejected    bigint[];
  v_existing    bigint[];
  v_new_ids     bigint[];
  v_merged      bigint[];
  v_inserted    integer := 0;
begin
  if v_user_id is null then
    raise exception 'not_authenticated' using errcode = '42501';
  end if;

  insert into private.sync_rate_limits as rl (user_id, window_start, calls)
  values (v_user_id, now(), 1)
  on conflict (user_id) do update set
    window_start = case when rl.window_start <= now() - c_window then now() else rl.window_start end,
    calls        = case when rl.window_start <= now() - c_window then 1 else rl.calls + 1 end
  returning calls into v_calls;

  if v_calls > c_max_calls then
    raise exception 'rate_limited' using errcode = 'PT429', hint = 'Too many sync calls; retry later.';
  end if;

  if coalesce(cardinality(p_achievement_ids), 0) > 256 then
    raise exception 'batch_too_large' using errcode = 'P0001', hint = 'Send at most 256 achievement ids per call.';
  end if;

  if p_game_id is null or not exists (select 1 from public.games g where g.id = p_game_id) then
    raise exception 'unknown_game' using errcode = 'P0001';
  end if;

  select coalesce(array_agg(a.id order by a.id), '{}')
    into v_valid_ids
    from public.achievements a
   where a.game_id = p_game_id
     and not a.is_retired
     and a.id = any(p_achievement_ids);

  select coalesce(array_agg(distinct req.id order by req.id), '{}')
    into v_rejected
    from unnest(p_achievement_ids) as req(id)
   where req.id is not null
     and not (req.id = any(v_valid_ids));

  select coalesce(ua.achievement_ids, '{}')
    into v_existing
    from public.user_achievements ua
   where ua.user_id = v_user_id
     and ua.game_id = p_game_id;

  if v_existing is null then
    v_existing := '{}';
  end if;

  select coalesce(array_agg(v.id order by v.id), '{}')
    into v_new_ids
    from unnest(v_valid_ids) as v(id)
   where not (v.id = any(v_existing));

  v_inserted := coalesce(cardinality(v_new_ids), 0);

  select coalesce(array_agg(distinct u.id order by u.id), '{}')
    into v_merged
    from unnest(v_existing || v_new_ids) as u(id);

  insert into public.user_achievements (user_id, game_id, achievement_ids, updated_at)
  values (v_user_id, p_game_id, v_merged, now())
  on conflict (user_id, game_id) do update set
    achievement_ids = excluded.achievement_ids,
    updated_at = now();

  return jsonb_build_object(
    'accepted', to_jsonb(v_valid_ids),
    'rejected', to_jsonb(v_rejected),
    'inserted', v_inserted
  );
end;
$$;

revoke all on function private.sync_achievements(bigint, bigint[]) from public;
grant execute on function private.sync_achievements(bigint, bigint[]) to authenticated;
