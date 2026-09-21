-- Intentionally empty.
--
-- This migration once tried to create public.user_profiles and a readable view. public.user_profiles
-- already exists in the live project with a different shape (used by patreon-user-info), and the view
-- referenced columns that do not exist, so it could never have applied. Do not recreate that table here.
--
-- The Patreon account -> auth user mapping and the readable view now live in
-- 20260920000000_patreon_identity.sql (public.patreon_identities).
select 1;
