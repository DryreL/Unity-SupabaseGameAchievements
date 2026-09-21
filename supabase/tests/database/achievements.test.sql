-- pgTAP tests for the achievements schema:
--   20260917000000_achievements.sql   catalog + sync/reconcile RPCs
--   20260920000000_patreon_identity.sql   Patreon identity columns, triggers, readable view
-- (the admin RPCs are covered by achievement_admin.test.sql)
--
-- Schema under test: user_achievements holds ONE row per (user, game) with the unlocked ids in a
-- bigint[] (achievement_ids); patreon_id / patreon_username / game_slug are filled by triggers.
-- Run with:  supabase test db
begin;

create extension if not exists pgtap with schema extensions;

select plan(69);

-- ---------------------------------------------------------------------------
-- Fixtures (as the migration owner)
-- ---------------------------------------------------------------------------

-- User 1 has a patreon_identities row. User 2 has none and relies on app_metadata (the fallback
-- path). User 4 is a second auth account for user 1's Patreon account (the duplicate-account bug the
-- identity migration exists to prevent).
insert into auth.users (id, email, raw_app_meta_data) values
  ('11111111-1111-1111-1111-111111111111', 'achievements-test-user-1@example.test', '{}'),
  ('22222222-2222-2222-2222-222222222222', 'achievements-test-user-2@example.test', '{"patreon_id":"patreon-2"}'),
  ('44444444-4444-4444-4444-444444444444', 'achievements-test-user-4@example.test', '{"patreon_id":"patreon-1"}');

insert into public.patreon_identities (user_id, patreon_id, patreon_username) values
  ('11111111-1111-1111-1111-111111111111', 'patreon-1', 'alice');

insert into public.games (id, slug, name) values
  (900001, 'test-game-a', 'Test Game A'),
  (900002, 'test-game-b', 'Test Game B');
insert into public.games (id, slug, name, is_active) values
  (900003, 'test-game-unreleased', 'Unreleased', false);

insert into public.achievements (id, game_id, achievement_key, title, description, bit_index) values
  (910001, 900001, 'first_blood',   'First Blood',   'Defeat your first enemy.', 0),
  (910002, 900001, 'lava_walker',   'Lava Walker',   'Cross the lava field.',    1),
  (910003, 900001, 'retired_thing', 'Retired',       'No longer obtainable.',    2),
  (920001, 900002, 'b_only',        'Game B Only',   'Belongs to game B.',       0),
  (930001, 900003, 'secret',        'Secret',        'Unreleased game.',         0);

update public.achievements set is_retired = true where id = 910003;

-- ---------------------------------------------------------------------------
-- Structure: RLS on, no client write grants, definer/invoker split
-- ---------------------------------------------------------------------------

select ok((select relrowsecurity from pg_class where oid = 'public.games'::regclass), 'RLS enabled on games');
select ok((select relrowsecurity from pg_class where oid = 'public.achievements'::regclass), 'RLS enabled on achievements');
select ok((select relrowsecurity from pg_class where oid = 'public.user_achievements'::regclass), 'RLS enabled on user_achievements');
select ok((select relrowsecurity from pg_class where oid = 'public.patreon_identities'::regclass), 'RLS enabled on patreon_identities');

select ok(not has_table_privilege('anon', 'public.user_achievements', 'select'), 'anon has no SELECT grant on user_achievements');
select ok(has_table_privilege('authenticated', 'public.user_achievements', 'select'), 'authenticated can SELECT user_achievements (RLS-scoped)');
select ok(not has_table_privilege('authenticated', 'public.user_achievements', 'insert'), 'authenticated has no INSERT grant on user_achievements');
select ok(not has_table_privilege('authenticated', 'public.user_achievements', 'update'), 'authenticated has no UPDATE grant on user_achievements');
select ok(not has_table_privilege('authenticated', 'public.user_achievements', 'delete'), 'authenticated has no DELETE grant on user_achievements');
select ok(not has_table_privilege('authenticated', 'public.achievements', 'insert'), 'authenticated has no INSERT grant on achievements');
select ok(not has_table_privilege('anon', 'public.games', 'update'), 'anon has no UPDATE grant on games');
select ok(not has_table_privilege('authenticated', 'public.patreon_identities', 'insert'), 'authenticated has no INSERT grant on patreon_identities');
select ok(not has_table_privilege('authenticated', 'public.patreon_identities', 'update'), 'authenticated has no UPDATE grant on patreon_identities');
select ok(not has_table_privilege('anon', 'public.patreon_identities', 'select'), 'anon has no SELECT grant on patreon_identities');
select ok(not has_table_privilege('anon', 'public.user_achievements_readable', 'select'), 'anon cannot read the readable view');
select ok(has_table_privilege('authenticated', 'public.user_achievements_readable', 'select'), 'authenticated can read the readable view');
select ok(
  (select coalesce(reloptions @> array['security_invoker=true'], false) from pg_class where oid = 'public.user_achievements_readable'::regclass),
  'the readable view is security_invoker, so it cannot bypass user_achievements RLS');
select ok(not has_function_privilege('anon', 'public.sync_achievements(bigint, bigint[])', 'execute'), 'anon cannot execute sync_achievements');
select ok(has_function_privilege('authenticated', 'public.sync_achievements(bigint, bigint[])', 'execute'), 'authenticated can execute sync_achievements');
select ok(not has_function_privilege('anon', 'public.get_my_achievements(bigint)', 'execute'), 'anon cannot execute get_my_achievements');
select ok((select prosecdef from pg_proc where oid = 'private.sync_achievements(bigint, bigint[])'::regprocedure), 'private.sync_achievements is security definer');
select ok(not (select prosecdef from pg_proc where oid = 'public.sync_achievements(bigint, bigint[])'::regprocedure), 'public.sync_achievements wrapper is security invoker');
select ok((select proconfig @> array['search_path=""'] from pg_proc where oid = 'private.sync_achievements(bigint, bigint[])'::regprocedure), 'definer function pins an empty search_path');

-- ---------------------------------------------------------------------------
-- Catalog invariants
-- ---------------------------------------------------------------------------

select throws_ok(
  $$ update public.achievements set bit_index = 7 where id = 910001 $$,
  'P0001', null, 'bit_index cannot be changed');
select throws_ok(
  $$ update public.achievements set achievement_key = 'renamed' where id = 910001 $$,
  'P0001', null, 'achievement_key cannot be changed');
select throws_ok(
  $$ delete from public.achievements where id = 910002 $$,
  'P0001', null, 'achievements cannot be hard-deleted');
select throws_ok(
  $$ insert into public.achievements (game_id, achievement_key, title, description, bit_index) values (900001, 'dupe_bit', 'D', 'D', 0) $$,
  '23505', null, 'bit_index is unique per game');
select lives_ok(
  $$ insert into public.achievements (game_id, achievement_key, title, description, bit_index) values (900002, 'same_bit_other_game', 'S', 'S', 1) $$,
  'the same bit_index is fine in another game');

select is((select catalog_version from public.games where id = 900002), 3, 'catalog_version bumps on every catalog insert');
update public.achievements set title = 'Lava Walker!' where id = 910002;
-- game A: 1 + 3 inserts + retire update + title update = 6 (the rejected statements above rolled back)
select is((select catalog_version from public.games where id = 900001), 6, 'catalog_version bumps on catalog update');

-- ---------------------------------------------------------------------------
-- anon
-- ---------------------------------------------------------------------------

set local role anon;
select set_config('request.jwt.claims', '{"role":"anon"}', true);

select throws_ok($$ select * from public.user_achievements $$, '42501', null, 'anon cannot read user_achievements');
select throws_ok($$ select * from public.user_achievements_readable $$, '42501', null, 'anon cannot read the readable view');
select throws_ok($$ select public.sync_achievements(900001, array[910001]::bigint[]) $$, '42501', null, 'anon cannot sync');
select is((select count(*)::int from public.games where id in (900001, 900002, 900003)), 2, 'anon sees only active games');
select is((select count(*)::int from public.achievements where id = 930001), 0, 'anon cannot see achievements of an inactive game');

reset role;

-- ---------------------------------------------------------------------------
-- User 1
-- ---------------------------------------------------------------------------

set local role authenticated;
select set_config('request.jwt.claims', '{"sub":"11111111-1111-1111-1111-111111111111","role":"authenticated"}', true);

select is(
  public.sync_achievements(900001, array[910001, 910002, 920001, 910003, 999999999, 910001, null]::bigint[]),
  '{"accepted":[910001,910002],"rejected":[910003,920001,999999999],"inserted":2}'::jsonb,
  'sync accepts own-game ids, rejects cross-game / retired / unknown ids, ignores duplicates and nulls');

select is(
  public.sync_achievements(900001, array[910001, 910002]::bigint[]),
  '{"accepted":[910001,910002],"rejected":[],"inserted":0}'::jsonb,
  'retrying the same batch is idempotent');

select is(
  (select achievement_ids from public.user_achievements where game_id = 900001),
  array[910001, 910002]::bigint[],
  'user 1 has one row for the game, holding both unlocked ids');

select is(
  public.sync_achievements(900002, array[910001]::bigint[]),
  '{"accepted":[],"rejected":[910001],"inserted":0}'::jsonb,
  'game A achievement cannot be unlocked through game B');

select is(
  public.sync_achievements(900001, array[]::bigint[]),
  '{"accepted":[],"rejected":[],"inserted":0}'::jsonb,
  'empty batch is a no-op');

select is(
  public.sync_achievements(900001, null),
  '{"accepted":[],"rejected":[],"inserted":0}'::jsonb,
  'null batch is a no-op');

select is(
  (select achievement_ids from public.user_achievements where game_id = 900001),
  array[910001, 910002]::bigint[],
  'rejected, empty and null batches never changed the unlocked ids');

select throws_ok($$ select public.sync_achievements(987654321, array[910001]::bigint[]) $$, 'P0001', 'unknown_game', 'unknown game is reported');
select throws_ok(
  $$ select public.sync_achievements(900001, (select array_agg(g) from generate_series(1, 257) g)::bigint[]) $$,
  'P0001', 'batch_too_large', 'oversized batches are rejected');

select throws_ok(
  $$ insert into public.user_achievements (user_id, game_id, achievement_ids) values ('11111111-1111-1111-1111-111111111111', 900002, array[920001]::bigint[]) $$,
  '42501', null, 'direct insert is denied');
select throws_ok(
  $$ insert into public.user_achievements (user_id, game_id, achievement_ids) values ('22222222-2222-2222-2222-222222222222', 900001, array[910001]::bigint[]) $$,
  '42501', null, 'direct insert on behalf of another user is denied');
select throws_ok(
  $$ update public.user_achievements set achievement_ids = array[910001, 910002, 910003]::bigint[] $$,
  '42501', null, 'unlocked ids cannot be rewritten directly');
select throws_ok($$ delete from public.user_achievements $$, '42501', null, 'direct delete is denied');
select throws_ok(
  $$ insert into public.patreon_identities (user_id, patreon_id) values ('11111111-1111-1111-1111-111111111111', 'patreon-x') $$,
  '42501', null, 'a client cannot write patreon_identities');

select is(
  (select array_agg(achievement_id order by achievement_id) from public.get_my_achievements(900001)),
  array[910001, 910002]::bigint[],
  'get_my_achievements returns own unlocks for the game');
select is((select count(*)::int from public.get_my_achievements(900002)), 0, 'get_my_achievements is scoped to the game');
select ok(
  (select bool_and(unlocked_at = now()) from public.get_my_achievements(900001)),
  'unlocked_at is the server time of the sync that stored the row');

select is(
  (select array_agg(achievement_key order by achievement_key) from public.user_achievements_readable),
  array['first_blood', 'lava_walker']::text[],
  'the readable view expands the id array into one row per unlock, with the catalog key');
select is(
  (select count(*)::int from public.patreon_identities), 1,
  'user 1 can read their own patreon identity');

reset role;

-- ---------------------------------------------------------------------------
-- Identity columns are filled by triggers, never by the client
-- ---------------------------------------------------------------------------

select is(
  (select row(patreon_id, patreon_username, game_slug)::text from public.user_achievements where user_id = '11111111-1111-1111-1111-111111111111' and game_id = 900001),
  '(patreon-1,alice,test-game-a)',
  'a user with a patreon_identities row gets patreon_id, username and game_slug on their rows');

update public.patreon_identities set patreon_username = 'alice2' where user_id = '11111111-1111-1111-1111-111111111111';
select is(
  (select patreon_username from public.user_achievements where user_id = '11111111-1111-1111-1111-111111111111' and game_id = 900001),
  'alice2',
  'a renamed Patreon profile propagates to the achievement rows');

-- ---------------------------------------------------------------------------
-- User 2: isolation, and the app_metadata fallback for the identity
-- ---------------------------------------------------------------------------

set local role authenticated;
select set_config('request.jwt.claims', '{"sub":"22222222-2222-2222-2222-222222222222","role":"authenticated"}', true);

select is((select count(*)::int from public.user_achievements), 0, 'user 2 cannot see user 1 rows');
select is((select count(*)::int from public.get_my_achievements(900001)), 0, 'user 2 reconciliation does not leak user 1 unlocks');
select is((select count(*)::int from public.user_achievements_readable), 0, 'user 2 cannot see user 1 unlocks through the readable view');
select is((select count(*)::int from public.patreon_identities), 0, 'user 2 cannot read user 1''s patreon identity');
select is(
  public.sync_achievements(900001, array[910001]::bigint[]),
  '{"accepted":[910001],"rejected":[],"inserted":1}'::jsonb,
  'user 2 can unlock the same achievement independently');

reset role;

select is(
  (select patreon_id from public.user_achievements where user_id = '22222222-2222-2222-2222-222222222222' and game_id = 900001),
  'patreon-2',
  'without a patreon_identities row, patreon_id falls back to the service-role-only app_metadata');

-- An authenticated role without a subject claim (e.g. malformed token) must not write.
set local role authenticated;
select set_config('request.jwt.claims', '{"role":"authenticated"}', true);
select throws_ok($$ select public.sync_achievements(900001, array[910001]::bigint[]) $$, '42501', 'not_authenticated', 'missing sub claim is rejected');
reset role;

select is(
  (select achievement_ids from public.user_achievements where user_id = '11111111-1111-1111-1111-111111111111' and game_id = 900001),
  array[910001, 910002]::bigint[],
  'user 2 syncing did not affect user 1');
select is(
  (select achievement_ids from public.user_achievements where user_id = '22222222-2222-2222-2222-222222222222' and game_id = 900001),
  array[910001]::bigint[],
  'rows are attributed to auth.uid(), never a client-provided id');

-- ---------------------------------------------------------------------------
-- One row per Patreon account per game, whatever auth user it signed in as
-- ---------------------------------------------------------------------------

select throws_ok(
  $$ insert into public.user_achievements (user_id, game_id, achievement_ids)
     values ('44444444-4444-4444-4444-444444444444', 900001, array[910002]::bigint[]) $$,
  '23505', null, 'a second auth user for the same Patreon account cannot hold a second row for the game');
-- Game C (900003) is used because user 1 already has a (possibly empty) row for game B.
select lives_ok(
  $$ insert into public.user_achievements (user_id, game_id, achievement_ids)
     values ('44444444-4444-4444-4444-444444444444', 900003, array[930001]::bigint[]) $$,
  'a Patreon account can still hold rows for other games');

-- ---------------------------------------------------------------------------
-- Syncing works for a game that is not active yet (only reading its catalog is hidden)
-- ---------------------------------------------------------------------------

set local role authenticated;
select set_config('request.jwt.claims', '{"sub":"22222222-2222-2222-2222-222222222222","role":"authenticated"}', true);
select is(
  public.sync_achievements(900003, array[930001]::bigint[]),
  '{"accepted":[930001],"rejected":[],"inserted":1}'::jsonb,
  'an unreleased game can be synced against');
select is((select count(*)::int from public.achievements where id = 930001), 0, 'but its catalog stays hidden from clients');
reset role;

select * from finish();
rollback;
