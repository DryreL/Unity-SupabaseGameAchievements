-- -----------------------------------------------------------------------------
-- Patreon Profiles & Readable Achievements View
-- -----------------------------------------------------------------------------

CREATE TABLE public.user_profiles (
    user_id UUID PRIMARY KEY REFERENCES auth.users(id) ON DELETE CASCADE,
    patreon_id TEXT,
    patreon_username TEXT,
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE public.user_profiles ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Users can read their own profile"
  ON public.user_profiles
  FOR SELECT
  TO authenticated
  USING ((SELECT auth.uid()) = user_id);

CREATE POLICY "Users can insert their own profile"
  ON public.user_profiles
  FOR INSERT
  TO authenticated
  WITH CHECK ((SELECT auth.uid()) = user_id);

CREATE POLICY "Users can update their own profile"
  ON public.user_profiles
  FOR UPDATE
  TO authenticated
  USING ((SELECT auth.uid()) = user_id);

GRANT SELECT, INSERT, UPDATE ON TABLE public.user_profiles TO authenticated;
REVOKE ALL ON TABLE public.user_profiles FROM anon;

CREATE VIEW public.user_achievements_readable AS
SELECT 
    up.patreon_username,
    up.patreon_id,
    ua.user_id,
    a.achievement_key,
    a.name AS achievement_name,
    ua.unlocked_at,
    a.game_id
FROM public.user_achievements ua
JOIN public.achievements a ON a.id = ua.achievement_id
LEFT JOIN public.user_profiles up ON up.user_id = ua.user_id;

GRANT SELECT ON public.user_achievements_readable TO authenticated;
