-- pgTAP tests for the catalog admin functions (20260918000000_achievement_admin.sql, with the
-- delete_retired_achievement fix of 20260920000000_patreon_identity.sql).
-- Run with:  supabase test db
begin;

create extension if not exists pgtap with schema extensions;

select plan(14);

-- ---------------------------------------------------------------------------
-- Fixtures (as the migration owner)
-- ---------------------------------------------------------------------------

insert into auth.users (id, email) values
  ('33333333-3333-3333-3333-333333333333', 'achievement-admin-test-user@example.test');

insert into public.games (id, slug, name) values (940001, 'admin-test-game', 'Admin Test Game');

insert into public.achievements (id, game_id, achievement_key, title, description, bit_index, is_retired) values
  (941001, 940001, 'live',               'Live',                'Still obtainable.',            0, false),
  (941002, 940001, 'earned_first',       'Earned (first)',      'Retired, first in the array.', 1, true),
  (941003, 940001, 'earned_second',      'Earned (second)',     'Retired, later in the array.', 2, true),
  (941004, 940001, 'never_earned',       'Never earned',        'Retired mistake.',             3, true);

-- One row per (user, game) holding an id array: 941002 and 941003 are earned, 941004 is not.
insert into public.user_achievements (user_id, game_id, achievement_ids)
values ('33333333-3333-3333-3333-333333333333', 940001, array[941002, 941003]::bigint[]);

-- ---------------------------------------------------------------------------
-- Privileges: service_role only
-- ---------------------------------------------------------------------------

select ok(not has_function_privilege('anon', 'public.delete_retired_achievement(bigint)', 'execute'), 'anon cannot delete');
select ok(not has_function_privilege('authenticated', 'public.delete_retired_achievement(bigint)', 'execute'), 'authenticated cannot delete');
select ok(has_function_privilege('service_role', 'public.delete_retired_achievement(bigint)', 'execute'), 'service_role can delete');
select ok(not has_function_privilege('authenticated', 'public.retire_achievement(bigint)', 'execute'), 'authenticated cannot retire');

-- ---------------------------------------------------------------------------
-- delete_retired_achievement, as service_role
-- ---------------------------------------------------------------------------

set local role service_role;

select throws_like(
  $$ select public.delete_retired_achievement(941001) $$,
  '%must be retired%', 'a live achievement cannot be deleted');

select throws_like(
  $$ select public.delete_retired_achievement(941002) $$,
  '%1 player unlock%', 'an unlock at the start of a player''s array blocks the delete');

select throws_like(
  $$ select public.delete_retired_achievement(941003) $$,
  '%1 player unlock%', 'an unlock later in a player''s array blocks the delete');

select throws_like(
  $$ select public.delete_retired_achievement(999999) $$,
  '%not found%', 'an unknown achievement is reported');

reset role;

select is(
  (select count(*)::int from public.achievements where id in (941001, 941002, 941003)),
  3, 'refused deletes left every row in place');

set local role service_role;

select lives_ok(
  $$ select public.delete_retired_achievement(941004) $$,
  'a retired achievement nobody unlocked can be deleted');

reset role;

select is(
  (select count(*)::int from public.achievements where id = 941004),
  0, 'the deleted achievement is gone');

select is(
  (select array_agg(achievement_ids order by user_id) from public.user_achievements where game_id = 940001),
  array[array[941002, 941003]]::bigint[][],
  'player unlocks are untouched by the delete');

-- Deleting frees the bit index, so a new achievement may take it (the documented, intended effect).
select lives_ok(
  $$ insert into public.achievements (game_id, achievement_key, title, description, bit_index)
     values (940001, 'reuses_bit_3', 'Reuses bit 3', 'Took the freed bit.', 3) $$,
  'the freed bit index can be reused');

-- retire_achievement stays idempotent.
set local role service_role;
select lives_ok(
  $$ select public.retire_achievement(941002) $$,
  'retiring an already retired achievement is a no-op');
reset role;

select * from finish();
rollback;
