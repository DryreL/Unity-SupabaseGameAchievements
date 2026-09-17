-- Web copies of achievement icons for the launcher, public profile pages and admin tools.
-- Games never download these at runtime; they ship their own packaged icons.
--
-- Layout:  achievement-assets/<game-slug>/<achievement_key>.webp
--          achievements.icon_path = '<game-slug>/<achievement_key>.webp'
--
-- Public bucket: objects are served from the public URL
--   <SUPABASE_URL>/storage/v1/object/public/achievement-assets/<icon_path>
-- No storage.objects policies are created, so clients can neither upload nor list; uploads are done
-- with the dashboard or the CLI (service role).

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('achievement-assets', 'achievement-assets', true, 262144, array['image/webp', 'image/png'])
on conflict (id) do update
  set public = excluded.public,
      file_size_limit = excluded.file_size_limit,
      allowed_mime_types = excluded.allowed_mime_types;
