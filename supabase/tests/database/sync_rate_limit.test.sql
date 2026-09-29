-- pgTAP tests for supabase/migrations/20260922000000_sync_rate_limit.sql
-- Run with:  supabase test db
begin;

create extension if not exists pgtap with schema extensions;

select plan(6);

insert into auth.users (id, email, raw_app_meta_data) values
  ('61111111-1111-1111-1111-111111111111', 'rate-limit-1@example.test', '{}'),
  ('62222222-2222-2222-2222-222222222222', 'rate-limit-2@example.test', '{}');
insert into public.games (id, slug, name) values (960001, 'rate-limit-game', 'Rate Limit Game');
insert into public.achievements (id, game_id, achievement_key, title, description, bit_index) values
  (961001, 960001, 'a', 'A', 'a', 0);

select ok(not has_table_privilege('authenticated', 'private.sync_rate_limits', 'select'), 'clients cannot read the counter table');

set local role authenticated;
select set_config('request.jwt.claims', '{"sub":"61111111-1111-1111-1111-111111111111","role":"authenticated"}', true);

select lives_ok($$
  select public.sync_achievements(960001, array[961001]::bigint[]) from generate_series(1, 60)
$$, '60 calls in a window are allowed');

select throws_ok($$ select public.sync_achievements(960001, array[961001]::bigint[]) $$, 'PT429', 'rate_limited', 'the 61st call is limited');

select set_config('request.jwt.claims', '{"sub":"62222222-2222-2222-2222-222222222222","role":"authenticated"}', true);
select lives_ok($$ select public.sync_achievements(960001, array[961001]::bigint[]) $$, 'another user is unaffected');

reset role;
update private.sync_rate_limits set window_start = now() - interval '2 minutes' where user_id = '61111111-1111-1111-1111-111111111111';

set local role authenticated;
select set_config('request.jwt.claims', '{"sub":"61111111-1111-1111-1111-111111111111","role":"authenticated"}', true);
select lives_ok($$ select public.sync_achievements(960001, array[961001]::bigint[]) $$, 'the window resets after a minute');
reset role;
select is((select calls from private.sync_rate_limits where user_id = '61111111-1111-1111-1111-111111111111'), 1, 'the counter restarted at 1');

select * from finish();
rollback;
