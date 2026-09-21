-- pgTAP tests for supabase/migrations/20260921000000_games_icon_style.sql
-- Run with:  supabase test db
begin;

create extension if not exists pgtap with schema extensions;

select plan(19);

insert into public.games (id, slug, name) values (950001, 'icon-style-game', 'Icon Style Game');
insert into public.achievements (id, game_id, achievement_key, title, description, bit_index) values
  (951001, 950001, 'a', 'A', 'a', 0);

-- Fixtures leave the game at catalog_version 2 (1 + the achievement insert).
select is((select catalog_version from public.games where id = 950001), 2, 'fixture starts at catalog_version 2');

-- ---------------------------------------------------------------------------
-- Defaults and constraints
-- ---------------------------------------------------------------------------

select is((select icon_style from public.games where id = 950001), 'combined', 'a game is combined by default');
select ok((select icon_background is null and icon_inset is null from public.games where id = 950001), 'background and inset default to NULL');

select throws_ok($$ update public.games set icon_style = 'hologram' where id = 950001 $$, '23514', null, 'an unknown style is rejected');
select throws_ok($$ update public.games set icon_inset = 0.5 where id = 950001 $$, '23514', null, 'an inset above 0.45 is rejected');
select throws_ok($$ update public.games set icon_inset = -0.1 where id = 950001 $$, '23514', null, 'a negative inset is rejected');
select throws_ok($$ update public.games set icon_background = '' where id = 950001 $$, '23514', null, 'an empty background path is rejected');
select lives_ok($$ update public.games set icon_inset = 0.45 where id = 950001 $$, 'the inset upper bound is allowed');

-- ---------------------------------------------------------------------------
-- catalog_version follows icon changes (and only icon changes)
-- ---------------------------------------------------------------------------

select is((select catalog_version from public.games where id = 950001), 3, 'changing the inset bumped catalog_version');

update public.games set icon_style = 'layered', icon_background = 'images/background' where id = 950001;
select is((select catalog_version from public.games where id = 950001), 4, 'switching to layered with a background bumped it once');

update public.games set icon_style = 'layered', icon_background = 'images/background' where id = 950001;
select is((select catalog_version from public.games where id = 950001), 4, 'writing the same values again does not bump');

update public.games set name = 'Renamed' where id = 950001;
select is((select catalog_version from public.games where id = 950001), 4, 'an unrelated change does not bump');

update public.games set icon_style = 'combined', catalog_version = 20 where id = 950001;
select is((select catalog_version from public.games where id = 950001), 20, 'a writer that sets catalog_version itself keeps its value');

insert into public.achievements (id, game_id, achievement_key, title, description, bit_index) values
  (951002, 950001, 'b', 'B', 'b', 1);
select is((select catalog_version from public.games where id = 950001), 21, 'catalog inserts still bump exactly once');

-- ---------------------------------------------------------------------------
-- Clients: read-only, and the trigger function is not callable
-- ---------------------------------------------------------------------------

select ok(has_column_privilege('anon', 'public.games', 'icon_style', 'select'), 'anon can read icon_style');
select ok(not has_table_privilege('anon', 'public.games', 'update'), 'anon still cannot update games');
select ok(not has_function_privilege('anon', 'private.bump_catalog_version_on_game_icon_change()', 'execute'), 'the trigger function is not callable by clients');

set local role anon;
select set_config('request.jwt.claims', '{"role":"anon"}', true);
select is((select icon_style from public.games where id = 950001), 'combined', 'anon reads the style of an active game');
select throws_ok($$ update public.games set icon_style = 'layered' where id = 950001 $$, '42501', null, 'anon cannot change the style');
reset role;

select * from finish();
rollback;
