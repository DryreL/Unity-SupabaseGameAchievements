// Supabase Edge Function for Patreon OAuth Middleware (Unified)
// Handles: OAuth redirect, token exchange, and API proxy
// Deploy: supabase functions deploy patreon-middleware
//
// Routes:
// - GET /?action=redirect (or just /) → Middleware HTML page
// - POST /?action=token-exchange → Exchange OAuth code for tokens
// - POST /?action=proxy → Proxy Patreon/Supabase API requests
// - POST /?action=supabase-session → Patreon token → Supabase Auth session (achievements/RLS)

import { serve } from "https://deno.land/std@0.168.0/http/server.ts"

// ============================================================================
// CORS Configuration (Shared)
// ============================================================================

/**
 * CORS Headers Generator
 * 
 * NOTE: This function allows ALL origins to prevent CORS issues when the game
 * is embedded on any website. This is necessary for games that can be hosted
 * on any domain (itch.io, Newgrounds, custom sites, etc.).
 * 
 * Security: POST requests still require SUPABASE_ANON_KEY in Authorization header.
 * GET requests (OAuth callbacks) are public by design.
 */
const getCorsHeaders = (origin: string | null, methods: string = 'GET, POST, OPTIONS') => {
  // Allow all origins - necessary for games hosted on any domain
  // If no origin header, use wildcard (but note: wildcard doesn't work with credentials)
  const corsOrigin = origin || '*'

  // Note: When using wildcard (*), Access-Control-Allow-Credentials must be false
  // But we need credentials for some requests, so we use the actual origin when available
  const headers: Record<string, string> = {
    'Access-Control-Allow-Origin': corsOrigin,
    'Access-Control-Allow-Methods': methods,
    'Access-Control-Allow-Headers': 'Content-Type, Authorization, User-Agent',
  }

  // Only set credentials if we have a specific origin (not wildcard)
  if (origin) {
    headers['Access-Control-Allow-Credentials'] = 'true'
  }

  return headers
}

// ============================================================================
// OAuth Redirect Configuration
// ============================================================================
//
// CRITICAL ARCHITECTURAL NOTE:
// Supabase Edge Functions DO NOT support serving HTML directly!
// Supabase API Gateway automatically rewrites any 'Content-Type: text/html' response
// to 'text/plain' and applies 'Content-Security-Policy: default-src none; sandbox'.
// Because of this, returning inline HTML causes browsers to render raw HTML code as text,
// preventing any JavaScript (<script>) from executing (breaking postMessage and OAuth callbacks).
//
// Therefore, the Edge Function MUST perform an HTTP 302/307 redirect to an external static
// HTML page hosted on a proper web server / CDN (viznitygames.com) that serves text/html.
const EXTERNAL_REDIRECT_BASE_URL = 'https://viznitygames.com/api/oauth/redirect/index.html'

function buildExternalRedirectUrl(url: URL): string {
  const redirectUrl = new URL(EXTERNAL_REDIRECT_BASE_URL)
  url.searchParams.forEach((value, key) => {
    // Preserve all incoming OAuth parameters (code, state, error, scheme, package, etc.)
    if (key !== 'action') {
      redirectUrl.searchParams.set(key, value)
    }
  })
  return redirectUrl.toString()
}

// ============================================================================
// SSO Handoff Handlers  (§3 — additive, do not modify existing handlers above)
// ============================================================================
//
// Cryptographic helpers: Web Crypto AES-GCM for payload encryption, SHA-256 for
// the ticket hash stored in the DB (so the raw ticket never rests on disk).

/** Base64url-encode an ArrayBuffer (no padding). */
function toBase64Url(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf)
  let b64 = ''
  for (const b of bytes) b64 += String.fromCharCode(b)
  return btoa(b64).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

/** Base64url-decode to Uint8Array. */
function fromBase64Url(s: string): Uint8Array {
  const padded = s.replace(/-/g, '+').replace(/_/g, '/').padEnd(s.length + (4 - s.length % 4) % 4, '=')
  const bin = atob(padded)
  return Uint8Array.from(bin, c => c.charCodeAt(0))
}

/** Hex-encode a Uint8Array. */
function toHex(buf: Uint8Array): string {
  return Array.from(buf).map(b => b.toString(16).padStart(2, '0')).join('')
}

/** Derive a fixed CryptoKey from the HANDOFF_ENC_KEY env secret (32 raw bytes, base64). */
async function getEncKey(): Promise<CryptoKey> {
  const secret = Deno.env.get('HANDOFF_ENC_KEY')
  if (!secret) throw new Error('HANDOFF_ENC_KEY is not configured')
  const raw = fromBase64Url(secret.replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_'))
  return crypto.subtle.importKey('raw', raw, { name: 'AES-GCM' }, false, ['encrypt', 'decrypt'])
}

/** AES-GCM encrypt a plain-text string; returns base64url(iv + ciphertext). */
async function encryptPayload(plain: string): Promise<string> {
  const key = await getEncKey()
  const iv = crypto.getRandomValues(new Uint8Array(12))
  const ct = await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, key, new TextEncoder().encode(plain))
  const combined = new Uint8Array(12 + ct.byteLength)
  combined.set(iv)
  combined.set(new Uint8Array(ct), 12)
  return toBase64Url(combined.buffer)
}

/** AES-GCM decrypt; input is base64url(iv + ciphertext). */
async function decryptPayload(encoded: string): Promise<string> {
  const key = await getEncKey()
  const combined = fromBase64Url(encoded)
  const iv = combined.slice(0, 12)
  const ct = combined.slice(12)
  const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv }, key, ct)
  return new TextDecoder().decode(plain)
}

/** SHA-256 hex digest of the raw ticket string. */
async function sha256Hex(s: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(s))
  return toHex(new Uint8Array(digest))
}

// ---------------------------------------------------------------------------
// action=create-handoff
// ---------------------------------------------------------------------------
//
// Body: { access_token, refresh_token?, expires_in?, game_id?, patreon_id? }
// Returns: { ticket, expires_at }  (120-second TTL, single-use)
// Security notes:
//   - Does NOT log the raw ticket or tokens.
//   - Opportunistic GC of expired rows (no pg_cron needed).

async function handleCreateHandoff(
  req: Request,
  corsHeaders: Record<string, string>,
  supabaseAnonKey: string | undefined,
): Promise<Response> {
  // Auth already verified by the outer gate; double-check for defence-in-depth.
  const authHeader = req.headers.get('authorization')
  if (!verifyAnonKey(authHeader, supabaseAnonKey)) {
    return new Response(JSON.stringify({ error: 'Unauthorized' }), {
      status: 401, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  let body: Record<string, unknown>
  try { body = await req.json() } catch {
    return new Response(JSON.stringify({ error: 'Invalid JSON body' }), {
      status: 400, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  const access_token = body.access_token as string | undefined
  if (!access_token) {
    return new Response(JSON.stringify({ error: 'missing access_token' }), {
      status: 400, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  const supabaseUrl = Deno.env.get('SUPABASE_URL')
  const serviceKey = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')
  if (!supabaseUrl || !serviceKey) {
    return new Response(JSON.stringify({ error: 'Server configuration error' }), {
      status: 500, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  // Encrypt the token payload at rest.
  const plainPayload = JSON.stringify({
    access_token,
    refresh_token: body.refresh_token ?? null,
    expires_in: body.expires_in ?? null,
  })
  let encPayload: string
  try { encPayload = await encryptPayload(plainPayload) } catch (e) {
    console.error('[create-handoff] encryption failed:', (e as Error).message)
    return new Response(JSON.stringify({ error: 'Server configuration error', message: 'HANDOFF_ENC_KEY missing or invalid' }), {
      status: 500, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  // Generate the raw ticket (opaque 32-byte random value) and store only its hash.
  const rawTicketBytes = crypto.getRandomValues(new Uint8Array(32))
  const rawTicket = toBase64Url(rawTicketBytes.buffer)
  const ticketHash = await sha256Hex(rawTicket)

  const expiresAt = new Date(Date.now() + 120_000).toISOString()  // 120-second TTL

  // Opportunistic GC of already-expired rows (best-effort; ignore failures).
  try {
    await fetch(`${supabaseUrl}/rest/v1/game_handoffs?expires_at=lt.${encodeURIComponent(new Date().toISOString())}`, {
      method: 'DELETE',
      headers: { apikey: serviceKey, Authorization: `Bearer ${serviceKey}` },
    })
  } catch { /* non-fatal */ }

  // Insert the new handoff row.
  const insertResp = await fetch(`${supabaseUrl}/rest/v1/game_handoffs`, {
    method: 'POST',
    headers: {
      apikey: serviceKey,
      Authorization: `Bearer ${serviceKey}`,
      'Content-Type': 'application/json',
      Prefer: 'return=minimal',
    },
    body: JSON.stringify({
      ticket_hash: ticketHash,
      game_id: body.game_id ?? null,
      patreon_id: body.patreon_id ?? null,
      payload: encPayload,
      expires_at: expiresAt,
    }),
  })

  if (!insertResp.ok) {
    const err = await insertResp.text()
    console.error('[create-handoff] DB insert failed:', insertResp.status, err.substring(0, 200))
    return new Response(JSON.stringify({ error: 'Failed to create handoff' }), {
      status: 500, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  // Return the raw ticket to the caller — this is the one and only time it leaves the server.
  return new Response(JSON.stringify({ ticket: rawTicket, expires_at: expiresAt }), {
    status: 200, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
  })
}

// ---------------------------------------------------------------------------
// action=redeem-handoff
// ---------------------------------------------------------------------------
//
// Body: { ticket, game_id? }
// Returns: { access_token, refresh_token, expires_in, patreon_id }  on success
//          401 { error: 'invalid_ticket' }  on any failure (uniform; no oracle)
// Security notes:
//   - Marks consumed_at BEFORE returning (conditional UPDATE on consumed_at IS NULL).
//   - Returns uniform 401 for missing / expired / already-consumed tickets.

async function handleRedeemHandoff(
  req: Request,
  corsHeaders: Record<string, string>,
  supabaseAnonKey: string | undefined,
): Promise<Response> {
  const authHeader = req.headers.get('authorization')
  if (!verifyAnonKey(authHeader, supabaseAnonKey)) {
    return new Response(JSON.stringify({ error: 'Unauthorized' }), {
      status: 401, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  let body: Record<string, unknown>
  try { body = await req.json() } catch {
    return new Response(JSON.stringify({ error: 'invalid_ticket' }), {
      status: 401, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  const rawTicket = body.ticket as string | undefined
  if (!rawTicket) {
    return new Response(JSON.stringify({ error: 'invalid_ticket' }), {
      status: 401, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  const supabaseUrl = Deno.env.get('SUPABASE_URL')
  const serviceKey = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')
  if (!supabaseUrl || !serviceKey) {
    return new Response(JSON.stringify({ error: 'invalid_ticket' }), {
      status: 401, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  const ticketHash = await sha256Hex(rawTicket)

  // Look up the row.
  const lookupResp = await fetch(
    `${supabaseUrl}/rest/v1/game_handoffs?ticket_hash=eq.${encodeURIComponent(ticketHash)}&select=*&limit=1`,
    { headers: { apikey: serviceKey, Authorization: `Bearer ${serviceKey}` } },
  )
  if (!lookupResp.ok) {
    return new Response(JSON.stringify({ error: 'invalid_ticket' }), {
      status: 401, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }
  const rows: Record<string, unknown>[] = await lookupResp.json()
  const row = rows[0]

  // Validate: must exist, not consumed, not expired.
  if (!row
    || row.consumed_at != null
    || new Date(row.expires_at as string) < new Date()
  ) {
    return new Response(JSON.stringify({ error: 'invalid_ticket' }), {
      status: 401, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  // Mark as consumed BEFORE decrypting/returning — race-safe conditional UPDATE.
  const consumeResp = await fetch(
    `${supabaseUrl}/rest/v1/game_handoffs?ticket_hash=eq.${encodeURIComponent(ticketHash)}&consumed_at=is.null`,
    {
      method: 'PATCH',
      headers: {
        apikey: serviceKey,
        Authorization: `Bearer ${serviceKey}`,
        'Content-Type': 'application/json',
        Prefer: 'return=minimal',
      },
      body: JSON.stringify({ consumed_at: new Date().toISOString() }),
    },
  )
  // If 0 rows were updated another request beat us to it (double-redeem) — reject.
  const updated = consumeResp.headers.get('content-range') // e.g. '0-0/1' or '*/0'
  if (!consumeResp.ok || (updated && updated.endsWith('/0'))) {
    return new Response(JSON.stringify({ error: 'invalid_ticket' }), {
      status: 401, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  // Decrypt payload.
  let tokenPayload: Record<string, unknown>
  try {
    const plain = await decryptPayload(row.payload as string)
    tokenPayload = JSON.parse(plain)
  } catch {
    return new Response(JSON.stringify({ error: 'invalid_ticket' }), {
      status: 401, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  return new Response(
    JSON.stringify({
      access_token: tokenPayload.access_token,
      refresh_token: tokenPayload.refresh_token ?? null,
      expires_in: tokenPayload.expires_in ?? null,
      patreon_id: row.patreon_id ?? null,
    }),
    { status: 200, headers: { ...corsHeaders, 'Content-Type': 'application/json' } },
  )
}

// ---------------------------------------------------------------------------
// action=token-refresh
// ---------------------------------------------------------------------------
//
// Body: { refresh_token }
// Returns: same shape as token-exchange (access_token, refresh_token, expires_in, …)
// Why needed: action=proxy forwards client_id/client_secret FROM the caller; this handler
// uses the server-held PATREON_CLIENT_ID/SECRET, making it the correct desktop refresh path.
// Bonus: migration route off the game's embedded AES-encrypted client_secret.

async function handleTokenRefresh(
  req: Request,
  corsHeaders: Record<string, string>,
  supabaseAnonKey: string | undefined,
): Promise<Response> {
  const authHeader = req.headers.get('authorization')
  if (!verifyAnonKey(authHeader, supabaseAnonKey)) {
    return new Response(JSON.stringify({ error: 'Unauthorized' }), {
      status: 401, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  let body: Record<string, unknown>
  try { body = await req.json() } catch {
    return new Response(JSON.stringify({ error: 'Invalid JSON body' }), {
      status: 400, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  const refresh_token = body.refresh_token as string | undefined
  if (!refresh_token) {
    return new Response(JSON.stringify({ error: 'Missing required parameter', message: '"refresh_token" is required' }), {
      status: 400, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  const patreonClientId = Deno.env.get('PATREON_CLIENT_ID')
  const patreonClientSecret = Deno.env.get('PATREON_CLIENT_SECRET')
  if (!patreonClientId || !patreonClientSecret) {
    return new Response(JSON.stringify({ error: 'Server configuration error', message: 'Patreon credentials not configured' }), {
      status: 500, headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  const formData = new URLSearchParams({
    grant_type: 'refresh_token',
    refresh_token,
    client_id: patreonClientId,
    client_secret: patreonClientSecret,
  })

  // Mirrors the retry logic in handleTokenExchange.
  let tokenResponse: Response | null = null
  let responseText = ''
  const maxRetries = 3
  for (let attempt = 0; attempt < maxRetries; attempt++) {
    try {
      tokenResponse = await fetch('https://www.patreon.com/api/oauth2/token', {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'User-Agent': 'Viznity Launcher' },
        body: formData,
      })
      responseText = await tokenResponse.text()
      if (tokenResponse.ok) break
      if (tokenResponse.status === 503 && attempt < maxRetries - 1) {
        await new Promise(r => setTimeout(r, (attempt + 1) * 1000))
        continue
      }
      break
    } catch (fetchErr) {
      if (attempt < maxRetries - 1) {
        await new Promise(r => setTimeout(r, (attempt + 1) * 1000))
        continue
      }
      return new Response(
        JSON.stringify({ error: 'Network error', message: `Failed to reach Patreon API: ${(fetchErr as Error).message}` }),
        { status: 503, headers: { ...corsHeaders, 'Content-Type': 'application/json' } },
      )
    }
  }

  if (!tokenResponse?.ok) {
    if (tokenResponse?.status === 503) {
      return new Response(
        JSON.stringify({ error: 'service_unavailable', error_description: 'Patreon API temporarily unavailable.', retry_after: 60 }),
        { status: 503, headers: { ...corsHeaders, 'Content-Type': 'application/json', 'Retry-After': '60' } },
      )
    }
    return new Response(responseText || JSON.stringify({ error: 'token_refresh_failed' }), {
      status: tokenResponse?.status || 500,
      headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  try {
    const data = JSON.parse(responseText)
    return new Response(
      JSON.stringify({
        access_token: data.access_token,
        refresh_token: data.refresh_token,
        expires_in: data.expires_in,
        scope: data.scope,
        token_type: data.token_type,
      }),
      { status: 200, headers: { ...corsHeaders, 'Content-Type': 'application/json' } },
    )
  } catch (parseErr) {
    return new Response(
      JSON.stringify({ error: 'Failed to parse token response', message: (parseErr as Error).message }),
      { status: 500, headers: { ...corsHeaders, 'Content-Type': 'application/json' } },
    )
  }
}

// ============================================================================
// action=supabase-session  (additive)
// ============================================================================
//
// Body:    { patreon_access_token }
// Returns: { access_token, refresh_token, expires_in, expires_at, token_type, user_id }
//
// Bridges a Patreon identity to a regular Supabase Auth session so database features (the
// achievement system first) can rely on auth.uid() + RLS instead of trusting client-supplied ids.
//
//   1. Verify the Patreon token by calling Patreon's identity endpoint → Patreon user id.
//   2. Find-or-create one Supabase Auth user per Patreon id. The email is synthetic and never
//      receives mail; the link to Patreon lives in app_metadata.patreon_id, which only the
//      service role can write. A user whose app_metadata does not match is refused, so an account
//      someone self-registered with the synthetic address can never be hijacked into.
//   3. Mint a session with admin generate_link + verify (the documented server-side way to issue a
//      session for a user without a password). No email is sent by either call.
//
// After this one call, clients refresh the Supabase session directly against
// /auth/v1/token?grant_type=refresh_token — this function is not on the refresh path.
//
// Secrets: SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY, SUPABASE_ANON_KEY are auto-injected.
// SUPABASE_SECRET_KEYS (auto-injected JSON, {"default": "sb_secret_..."}) enables Sb-Forwarded-For so
// Auth's per-IP rate limits apply to the real client instead of this function's shared egress IP.
// (Custom secrets cannot start with SUPABASE_, so there is nothing to configure for this.)
// Optional: IDENTITY_EMAIL_DOMAIN (default below). Changing it after launch orphans existing users.
// Never logs tokens.

const DEFAULT_IDENTITY_EMAIL_DOMAIN = 'patreon.users.viznitygames.com'

function defaultSecretKey(): string | undefined {
  try {
    const keys = JSON.parse(Deno.env.get('SUPABASE_SECRET_KEYS') ?? '{}') as Record<string, unknown>
    const key = keys.default ?? Object.values(keys)[0]
    return typeof key === 'string' && key.length > 0 ? key : undefined
  } catch {
    return undefined
  }
}

function jsonResponse(status: number, body: unknown, corsHeaders: Record<string, string>, extra: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { ...corsHeaders, 'Content-Type': 'application/json', 'Cache-Control': 'no-store', ...extra },
  })
}

async function handleSupabaseSession(
  req: Request,
  corsHeaders: Record<string, string>,
  supabaseAnonKey: string | undefined,
): Promise<Response> {
  if (!verifyAnonKey(req.headers.get('authorization'), supabaseAnonKey)) {
    return jsonResponse(401, { error: 'Unauthorized' }, corsHeaders)
  }

  let body: Record<string, unknown>
  try { body = await req.json() } catch {
    return jsonResponse(400, { error: 'invalid_json' }, corsHeaders)
  }

  const patreonToken = body.patreon_access_token
  if (typeof patreonToken !== 'string' || patreonToken.length < 10 || patreonToken.length > 4096) {
    return jsonResponse(400, { error: 'missing_patreon_access_token' }, corsHeaders)
  }

  const supabaseUrl = Deno.env.get('SUPABASE_URL')
  const serviceKey = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')
  const secretKey = defaultSecretKey()
  if (!supabaseUrl || !serviceKey || !supabaseAnonKey) {
    return jsonResponse(500, { error: 'server_configuration_error' }, corsHeaders)
  }

  // 1. Who is this? Ask Patreon, never the client.
  let patreonId: string
  try {
    const identity = await fetch('https://www.patreon.com/api/oauth2/v2/identity', {
      headers: { Authorization: `Bearer ${patreonToken}`, 'User-Agent': 'Viznity Launcher' },
      signal: AbortSignal.timeout(10_000),
    })
    if (identity.status === 401 || identity.status === 403) {
      return jsonResponse(401, { error: 'invalid_patreon_token' }, corsHeaders)
    }
    if (!identity.ok) {
      return jsonResponse(503, { error: 'identity_provider_unavailable', retry_after: 60 }, corsHeaders, { 'Retry-After': '60' })
    }
    const payload = await identity.json()
    const id = payload?.data?.id
    if (typeof id !== 'string' || !/^[0-9]{1,32}$/.test(id)) {
      return jsonResponse(502, { error: 'unexpected_identity_response' }, corsHeaders)
    }
    patreonId = id
  } catch {
    return jsonResponse(503, { error: 'identity_provider_unavailable', retry_after: 30 }, corsHeaders, { 'Retry-After': '30' })
  }

  const domain = Deno.env.get('IDENTITY_EMAIL_DOMAIN') || DEFAULT_IDENTITY_EMAIL_DOMAIN
  const email = `patreon-${patreonId}@${domain}`
  const adminHeaders = {
    apikey: serviceKey,
    Authorization: `Bearer ${serviceKey}`,
    'Content-Type': 'application/json',
  }

  try {
    // 2. Find-or-create. 422 email_exists is the normal "already created" path.
    const created = await fetch(`${supabaseUrl}/auth/v1/admin/users`, {
      method: 'POST',
      headers: adminHeaders,
      body: JSON.stringify({ email, email_confirm: true, app_metadata: { patreon_id: patreonId } }),
      signal: AbortSignal.timeout(10_000),
    })
    if (!created.ok && created.status !== 422) {
      console.error('[supabase-session] create user failed:', created.status)
      return jsonResponse(503, { error: 'auth_unavailable', retry_after: 30 }, corsHeaders, { 'Retry-After': '30' })
    }
    await created.body?.cancel()

    // 3a. One-time token for that user (no email is sent by the admin endpoint).
    const link = await fetch(`${supabaseUrl}/auth/v1/admin/generate_link`, {
      method: 'POST',
      headers: adminHeaders,
      body: JSON.stringify({ type: 'magiclink', email }),
      signal: AbortSignal.timeout(10_000),
    })
    if (!link.ok) {
      console.error('[supabase-session] generate_link failed:', link.status)
      const status = link.status === 429 ? 429 : 503
      return jsonResponse(status, { error: 'auth_unavailable', retry_after: 60 }, corsHeaders, { 'Retry-After': '60' })
    }
    const linkData = await link.json()
    if (linkData?.app_metadata?.patreon_id !== patreonId) {
      // Someone registered the synthetic address without going through this function.
      console.error('[supabase-session] identity conflict for a synthetic identity email')
      return jsonResponse(409, { error: 'identity_conflict' }, corsHeaders)
    }
    const tokenHash = linkData?.hashed_token ?? linkData?.properties?.hashed_token
    if (typeof tokenHash !== 'string') {
      return jsonResponse(502, { error: 'unexpected_auth_response' }, corsHeaders)
    }

    // 3b. Exchange it for a session. Forward the caller's IP when a secret key is configured.
    const verifyHeaders: Record<string, string> = { 'Content-Type': 'application/json', apikey: supabaseAnonKey }
    const clientIp = (req.headers.get('x-forwarded-for') || '').split(',')[0].trim()
    if (secretKey && clientIp) {
      verifyHeaders.apikey = secretKey
      verifyHeaders['Sb-Forwarded-For'] = clientIp
    }
    const verified = await fetch(`${supabaseUrl}/auth/v1/verify`, {
      method: 'POST',
      headers: verifyHeaders,
      body: JSON.stringify({ type: 'magiclink', token_hash: tokenHash }),
      signal: AbortSignal.timeout(10_000),
    })
    if (!verified.ok) {
      console.error('[supabase-session] verify failed:', verified.status)
      const status = verified.status === 429 ? 429 : 503
      return jsonResponse(status, { error: 'auth_unavailable', retry_after: 60 }, corsHeaders, { 'Retry-After': '60' })
    }
    const session = await verified.json()
    if (typeof session?.access_token !== 'string' || typeof session?.refresh_token !== 'string') {
      return jsonResponse(502, { error: 'unexpected_auth_response' }, corsHeaders)
    }

    return jsonResponse(200, {
      access_token: session.access_token,
      refresh_token: session.refresh_token,
      expires_in: session.expires_in,
      expires_at: session.expires_at,
      token_type: session.token_type ?? 'bearer',
      user_id: session.user?.id ?? linkData.id,
    }, corsHeaders)
  } catch (e) {
    console.error('[supabase-session] auth request failed:', (e as Error).name)
    return jsonResponse(503, { error: 'auth_unavailable', retry_after: 30 }, corsHeaders, { 'Retry-After': '30' })
  }
}

// ============================================================================
// Token Exchange Handler
// ============================================================================

interface TokenResponse {
  access_token: string
  refresh_token: string
  expires_in: number
  scope: string
  token_type: string
}

async function handleTokenExchange(req: Request, corsHeaders: Record<string, string>, supabaseAnonKey?: string): Promise<Response> {
  try {
    // Security: Verify SUPABASE_ANON_KEY
    const authHeader = req.headers.get('authorization')
    if (!verifyAnonKey(authHeader, supabaseAnonKey)) {
      console.error('[Token Exchange] ❌ SECURITY: Invalid or missing SUPABASE_ANON_KEY')
      return new Response(
        JSON.stringify({
          error: 'Unauthorized',
          message: 'Token exchange requires valid Authorization header with SUPABASE_ANON_KEY'
        }),
        {
          headers: { ...corsHeaders, 'Content-Type': 'application/json' },
          status: 401
        }
      )
    }

    console.log('[Token Exchange] ✅ Authorization verified')

    // Parse request body
    let body: any
    const contentType = req.headers.get('content-type') || ''

    if (contentType.includes('application/json')) {
      body = await req.json()
    } else if (contentType.includes('application/x-www-form-urlencoded')) {
      const formData = await req.formData()
      body = {
        code: formData.get('code'),
        redirect_uri: formData.get('redirect_uri')
      }
    } else {
      const text = await req.text()
      try {
        body = JSON.parse(text)
      } catch (e) {
        const formData = new URLSearchParams(text)
        body = {
          code: formData.get('code'),
          redirect_uri: formData.get('redirect_uri')
        }
      }
    }

    // Validate required parameters
    if (!body.code || !body.redirect_uri) {
      return new Response(
        JSON.stringify({
          error: 'Missing required parameters',
          message: 'Both "code" and "redirect_uri" are required'
        }),
        {
          headers: { ...corsHeaders, 'Content-Type': 'application/json' },
          status: 400
        }
      )
    }

    // Get Patreon credentials from environment variables
    const patreonClientId = Deno.env.get('PATREON_CLIENT_ID')
    const patreonClientSecret = Deno.env.get('PATREON_CLIENT_SECRET')

    if (!patreonClientId || !patreonClientSecret) {
      console.error('[Token Exchange] Missing Patreon credentials in environment variables')
      return new Response(
        JSON.stringify({
          error: 'Server configuration error',
          message: 'Patreon credentials not configured'
        }),
        {
          headers: { ...corsHeaders, 'Content-Type': 'application/json' },
          status: 500
        }
      )
    }

    console.log(`[Token Exchange] Exchanging code for token`)
    console.log(`[Token Exchange] Redirect URI: ${body.redirect_uri}`)
    console.log(`[Token Exchange] Redirect URI length: ${body.redirect_uri?.length || 0}`)
    console.log(`[Token Exchange] Redirect URI encoded: ${encodeURIComponent(body.redirect_uri || '')}`)
    console.log(`[Token Exchange] Client ID: ${patreonClientId ? patreonClientId.substring(0, 10) + '...' : 'MISSING'}`)

    // Normalize redirect URI (remove trailing slash if present, ensure exact match with Patreon)
    let normalizedRedirectUri = body.redirect_uri
    if (normalizedRedirectUri && normalizedRedirectUri.endsWith('/')) {
      normalizedRedirectUri = normalizedRedirectUri.slice(0, -1)
      console.log(`[Token Exchange] ⚠️ Redirect URI had trailing slash, normalized: ${normalizedRedirectUri}`)
    }

    // Build form data
    const formData = new URLSearchParams({
      grant_type: 'authorization_code',
      code: body.code,
      redirect_uri: normalizedRedirectUri,
      client_id: patreonClientId,
      client_secret: patreonClientSecret
    })

    console.log(`[Token Exchange] Sending to Patreon API:`)
    console.log(`[Token Exchange]   - grant_type: authorization_code`)
    console.log(`[Token Exchange]   - code: ${body.code ? body.code.substring(0, 10) + '...' : 'MISSING'}`)
    console.log(`[Token Exchange]   - redirect_uri: ${normalizedRedirectUri}`)
    console.log(`[Token Exchange]   - client_id: ${patreonClientId ? patreonClientId.substring(0, 10) + '...' : 'MISSING'}`)

    // Exchange authorization code for access token with retry mechanism
    let tokenResponse: Response | null = null
    let responseText = ''
    const maxRetries = 3
    let retryCount = 0
    let lastError: string | null = null

    while (retryCount < maxRetries) {
      try {
        tokenResponse = await fetch('https://www.patreon.com/api/oauth2/token', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/x-www-form-urlencoded',
            'User-Agent': 'Unity Patreon Integration'
          },
          body: formData
        })

        responseText = await tokenResponse.text()
        console.log(`[Token Exchange] Patreon API response: ${tokenResponse.status} (attempt ${retryCount + 1}/${maxRetries})`)

        // If successful, break out of retry loop
        if (tokenResponse.ok) {
          break
        }

        // If 503 Service Unavailable, retry after delay
        if (tokenResponse.status === 503 && retryCount < maxRetries - 1) {
          lastError = responseText
          const retryDelay = (retryCount + 1) * 1000 // Exponential backoff: 1s, 2s, 3s
          console.warn(`[Token Exchange] ⚠️ Patreon API returned 503 (Service Unavailable). Retrying in ${retryDelay}ms... (attempt ${retryCount + 1}/${maxRetries})`)
          await new Promise(resolve => setTimeout(resolve, retryDelay))
          retryCount++
          continue
        }

        // For other errors or final retry, break and return error
        break
      } catch (fetchError) {
        lastError = fetchError.message
        console.error(`[Token Exchange] Fetch error (attempt ${retryCount + 1}/${maxRetries}):`, fetchError)

        // Retry on network errors
        if (retryCount < maxRetries - 1) {
          const retryDelay = (retryCount + 1) * 1000
          console.warn(`[Token Exchange] ⚠️ Network error. Retrying in ${retryDelay}ms... (attempt ${retryCount + 1}/${maxRetries})`)
          await new Promise(resolve => setTimeout(resolve, retryDelay))
          retryCount++
          continue
        }

        // Final retry failed
        return new Response(
          JSON.stringify({
            error: 'Network error',
            message: `Failed to connect to Patreon API after ${maxRetries} attempts: ${fetchError.message}`
          }),
          {
            headers: { ...corsHeaders, 'Content-Type': 'application/json' },
            status: 503
          }
        )
      }
    }

    if (!tokenResponse || !tokenResponse.ok) {
      const errorMessage = lastError || responseText || 'Unknown error'
      console.error(`[Token Exchange] Patreon API error after ${retryCount + 1} attempts: ${errorMessage}`)

      // Return user-friendly error message for 503
      if (tokenResponse?.status === 503) {
        return new Response(
          JSON.stringify({
            error: 'service_unavailable',
            error_description: 'Patreon API is temporarily unavailable. Please try again in a few moments.',
            retry_after: 60 // Suggest retry after 60 seconds
          }),
          {
            status: 503,
            headers: {
              ...corsHeaders,
              'Content-Type': 'application/json',
              'Retry-After': '60'
            }
          }
        )
      }

      return new Response(
        responseText || JSON.stringify({
          error: 'token_exchange_failed',
          error_description: errorMessage
        }),
        {
          status: tokenResponse?.status || 500,
          headers: {
            ...corsHeaders,
            'Content-Type': 'application/json'
          }
        }
      )
    }

    // Parse token response
    try {
      const tokenData: TokenResponse = JSON.parse(responseText)
      console.log(`[Token Exchange] Token exchange successful`)

      return new Response(
        JSON.stringify({
          access_token: tokenData.access_token,
          refresh_token: tokenData.refresh_token,
          expires_in: tokenData.expires_in,
          scope: tokenData.scope,
          token_type: tokenData.token_type
        }),
        {
          status: 200,
          headers: {
            ...corsHeaders,
            'Content-Type': 'application/json'
          }
        }
      )
    } catch (parseError) {
      console.error('[Token Exchange] Failed to parse Patreon response:', parseError)
      return new Response(
        JSON.stringify({
          error: 'Failed to parse token response',
          message: parseError.message
        }),
        {
          headers: { ...corsHeaders, 'Content-Type': 'application/json' },
          status: 500
        }
      )
    }
  } catch (error) {
    console.error('[Token Exchange] Error:', error)
    return new Response(
      JSON.stringify({
        error: 'Token exchange failed',
        message: error.message
      }),
      {
        headers: { ...corsHeaders, 'Content-Type': 'application/json' },
        status: 500
      }
    )
  }
}

// ============================================================================
// Proxy Handler
// ============================================================================

async function handleProxy(req: Request, corsHeaders: Record<string, string>, supabaseAnonKey?: string): Promise<Response> {
  try {
    // Security: Verify SUPABASE_ANON_KEY
    const authHeader = req.headers.get('authorization')
    if (!verifyAnonKey(authHeader, supabaseAnonKey)) {
      console.error('[Proxy] ❌ SECURITY: Invalid or missing SUPABASE_ANON_KEY')
      return new Response(
        JSON.stringify({
          error: 'Unauthorized',
          message: 'Proxy requests require valid Authorization header with SUPABASE_ANON_KEY'
        }),
        {
          headers: { ...corsHeaders, 'Content-Type': 'application/json' },
          status: 401
        }
      )
    }

    console.log('[Proxy] ✅ Authorization verified')

    // Parse request body
    let body: any
    const contentType = req.headers.get('content-type') || ''

    if (contentType.includes('application/json')) {
      body = await req.json()
    } else if (contentType.includes('application/x-www-form-urlencoded')) {
      const formData = await req.formData()
      body = {}
      for (const [key, value] of formData.entries()) {
        body[key] = value
      }
    } else {
      const text = await req.text()
      try {
        body = JSON.parse(text)
      } catch (e) {
        const formData = new URLSearchParams(text)
        body = {}
        for (const [key, value] of formData.entries()) {
          body[key] = value
        }
      }
    }

    // Check if this is a POST body request
    const isTokenEndpoint = body.url && body.url.includes('/api/oauth2/token')
    const hasPatreonFormData = body.grant_type || body.refresh_token || body.code || body.client_id || body.client_secret
    const isSupabaseUrlCheck = body.url && (body.url.includes('.supabase.co/rest/v1/') || body.url.includes('.supabase.co/rest/v1'))
    const hasSupabaseBody = body.jsonBody || body.method === 'POST' || body.method === 'PATCH'

    // Handle Supabase POST/PATCH requests with JSON body
    if (isSupabaseUrlCheck && hasSupabaseBody && body.jsonBody) {
      let requestMethod = body.method || 'POST'

      const supabaseHeaders: Record<string, string> = {
        'Content-Type': 'application/json',
        'Accept': 'application/json',
        'User-Agent': 'Unity Supabase Integration'
      }

      if (body.apikey) supabaseHeaders['apikey'] = body.apikey

      // For POST requests, use upsert to prevent 409 conflicts
      // This automatically handles unique constraint violations
      if (requestMethod === 'POST' && !body.prefer) {
        // Use merge-duplicates to handle conflicts gracefully
        supabaseHeaders['Prefer'] = 'resolution=merge-duplicates'
      } else if (body.prefer) {
        supabaseHeaders['Prefer'] = body.prefer
      }

      console.log(`[Proxy] Forwarding ${requestMethod} request to Supabase: ${body.url}`)
      if (requestMethod === 'POST') {
        console.log(`[Proxy] Using upsert mode (resolution=merge-duplicates) to prevent 409 conflicts`)
      }

      let response = await fetch(body.url, {
        method: requestMethod,
        headers: supabaseHeaders,
        body: typeof body.jsonBody === 'string' ? body.jsonBody : JSON.stringify(body.jsonBody)
      })

      let responseText = await response.text()
      let contentTypeHeader = response.headers.get('content-type') || 'application/json'

      // If POST returns 409, automatically retry with PATCH (upsert fallback)
      if (response.status === 409 && requestMethod === 'POST') {
        console.warn(`[Proxy] ⚠️ 409 Conflict from Supabase POST, retrying with PATCH (upsert): ${body.url}`)
        console.warn(`[Proxy] Original response: ${responseText.substring(0, 200)}`)

        // Extract unique identifier from JSON body for PATCH
        let jsonBody = typeof body.jsonBody === 'string' ? JSON.parse(body.jsonBody) : body.jsonBody
        let uniqueKey = jsonBody.id || jsonBody.user_id || jsonBody.patreon_user_id

        if (uniqueKey) {
          // Retry with PATCH using the unique key
          const patchUrl = `${body.url}?id=eq.${uniqueKey}`
          console.log(`[Proxy] Retrying with PATCH to: ${patchUrl}`)

          // Remove resolution header for PATCH (PATCH is inherently an update)
          const patchHeaders = { ...supabaseHeaders }
          delete patchHeaders['Prefer']

          response = await fetch(patchUrl, {
            method: 'PATCH',
            headers: patchHeaders,
            body: typeof body.jsonBody === 'string' ? body.jsonBody : JSON.stringify(body.jsonBody)
          })

          responseText = await response.text()
          contentTypeHeader = response.headers.get('content-type') || 'application/json'

          if (response.status === 200 || response.status === 204) {
            console.log(`[Proxy] ✅ PATCH retry successful after 409 conflict`)
          } else {
            console.warn(`[Proxy] ⚠️ PATCH retry returned status ${response.status}`)
          }
        } else {
          console.warn(`[Proxy] ⚠️ Cannot retry with PATCH: No unique identifier found in request body`)
          console.warn(`[Proxy] Request body keys: ${Object.keys(jsonBody).join(', ')}`)
        }
      } else if (response.status === 409) {
        console.warn(`[Proxy] ⚠️ 409 Conflict from Supabase: ${body.url}`)
        console.warn(`[Proxy] Response body: ${responseText.substring(0, 500)}`)
        console.warn(`[Proxy] This usually means a unique constraint violation.`)
      }

      return new Response(
        responseText,
        {
          status: response.status,
          headers: {
            ...corsHeaders,
            'Content-Type': contentTypeHeader
          }
        }
      )
    }

    // Handle Patreon token endpoint POST requests
    if (isTokenEndpoint && hasPatreonFormData) {
      const patreonFormData = new URLSearchParams()
      if (body.grant_type) patreonFormData.append('grant_type', body.grant_type as string)
      if (body.code) patreonFormData.append('code', body.code as string)
      if (body.refresh_token) patreonFormData.append('refresh_token', body.refresh_token as string)
      if (body.client_id) patreonFormData.append('client_id', body.client_id as string)
      if (body.client_secret) patreonFormData.append('client_secret', body.client_secret as string)
      if (body.redirect_uri) patreonFormData.append('redirect_uri', body.redirect_uri as string)

      console.log(`[Proxy] Forwarding POST request to token endpoint: ${body.url}`)

      const response = await fetch(body.url, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/x-www-form-urlencoded',
          'Accept': 'application/json',
          'User-Agent': 'Unity Patreon Integration'
        },
        body: patreonFormData.toString()
      })

      const responseText = await response.text()
      const contentTypeHeader = response.headers.get('content-type') || 'application/json'

      return new Response(
        responseText,
        {
          status: response.status,
          headers: {
            ...corsHeaders,
            'Content-Type': contentTypeHeader
          }
        }
      )
    }

    // Regular GET request (Patreon or Supabase)
    if (!body.url) {
      return new Response(
        JSON.stringify({
          error: 'Missing required parameters',
          message: 'URL is required'
        }),
        {
          headers: { ...corsHeaders, 'Content-Type': 'application/json' },
          status: 400
        }
      )
    }

    if (!isSupabaseUrlCheck && !body.authorization) {
      return new Response(
        JSON.stringify({
          error: 'Missing required parameters',
          message: 'Authorization header is required for Patreon API requests'
        }),
        {
          headers: { ...corsHeaders, 'Content-Type': 'application/json' },
          status: 400
        }
      )
    }

    if (isSupabaseUrlCheck && !body.apikey) {
      return new Response(
        JSON.stringify({
          error: 'Missing required parameters',
          message: 'apikey is required for Supabase API requests'
        }),
        {
          headers: { ...corsHeaders, 'Content-Type': 'application/json' },
          status: 400
        }
      )
    }

    // Security: Only allow Patreon API URLs, Supabase URLs, or Patreon CDN image URLs
    const isPatreonUrl = body.url.startsWith('https://www.patreon.com/api/')
    const isPatreonImageUrl = body.url.startsWith('https://c10.patreonusercontent.com/') ||
      body.url.startsWith('https://c5.patreonusercontent.com/') ||
      body.url.startsWith('https://c8.patreonusercontent.com/') ||
      body.url.includes('patreonusercontent.com/')

    if (!isPatreonUrl && !isSupabaseUrlCheck && !isPatreonImageUrl) {
      return new Response(
        JSON.stringify({
          error: 'Invalid URL',
          message: 'Only Patreon API URLs, Supabase URLs, or Patreon CDN image URLs are allowed'
        }),
        {
          headers: { ...corsHeaders, 'Content-Type': 'application/json' },
          status: 400
        }
      )
    }

    // For image URLs, use appropriate Accept header
    const isImageRequest = isPatreonImageUrl || body.url.match(/\.(jpg|jpeg|png|gif|webp|svg)(\?|$)/i)

    console.log(`[Proxy] Forwarding ${isImageRequest ? 'IMAGE' : 'GET'} request to: ${body.url}`)

    const requestHeaders: Record<string, string> = {
      'Accept': isImageRequest ? 'image/*,*/*' : 'application/json',
      'User-Agent': 'Unity Integration'
    }

    if (body.authorization) {
      requestHeaders['Authorization'] = body.authorization
    }

    if (isSupabaseUrlCheck && body.apikey) {
      requestHeaders['apikey'] = body.apikey
    }

    if (isSupabaseUrlCheck && body.prefer) {
      requestHeaders['Prefer'] = body.prefer
    }

    const response = await fetch(body.url, {
      method: 'GET',
      headers: requestHeaders
    })

    // For image requests, return binary data; for JSON requests, return text
    let responseData: string | ArrayBuffer
    let contentTypeHeader = response.headers.get('content-type') || 'application/json'

    if (isImageRequest) {
      // For images, get as ArrayBuffer to preserve binary data
      const arrayBuffer = await response.arrayBuffer()
      responseData = arrayBuffer
      // Ensure correct content type for images
      if (!contentTypeHeader.startsWith('image/')) {
        contentTypeHeader = response.headers.get('content-type') || 'image/jpeg'
      }
    } else {
      // For JSON/text, get as text
      responseData = await response.text()
    }

    return new Response(
      responseData,
      {
        status: response.status,
        headers: {
          ...corsHeaders,
          'Content-Type': contentTypeHeader
        }
      }
    )
  } catch (error) {
    console.error('[Proxy] Error:', error)
    return new Response(
      JSON.stringify({
        error: 'Proxy request failed',
        message: error.message
      }),
      {
        headers: { ...corsHeaders, 'Content-Type': 'application/json' },
        status: 500
      }
    )
  }
}

// ============================================================================
// Player Profile Handlers (Viznity Desktop App cloud profile sync)
// ============================================================================
//
// Reads/writes the public.player_profiles table (see supabase/schema.sql). The service role
// key that bypasses Row Level Security never leaves this function — it uses SUPABASE_URL and
// SUPABASE_SERVICE_ROLE_KEY, which Supabase automatically injects into every Edge Function. The
// desktop app authenticates to this function with the public SUPABASE_ANON_KEY only, same as
// the token-exchange and proxy routes above.

async function handleGetProfile(url: URL, corsHeaders: Record<string, string>): Promise<Response> {
  const patreonId = url.searchParams.get('patreon_id')
  if (!patreonId) {
    return new Response(JSON.stringify({ error: 'Missing patreon_id' }), {
      status: 400,
      headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  const supabaseUrl = Deno.env.get('SUPABASE_URL')
  const serviceKey = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')
  if (!supabaseUrl || !serviceKey) {
    return new Response(
      JSON.stringify({ error: 'Server configuration error', message: 'Supabase service credentials not configured' }),
      { status: 500, headers: { ...corsHeaders, 'Content-Type': 'application/json' } },
    )
  }

  const response = await fetch(
    `${supabaseUrl}/rest/v1/player_profiles?patreon_id=eq.${encodeURIComponent(patreonId)}&select=*&limit=1`,
    { headers: { apikey: serviceKey, Authorization: `Bearer ${serviceKey}` } },
  )
  if (!response.ok) {
    return new Response(await response.text(), {
      status: response.status,
      headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }
  const rows = await response.json()
  return new Response(JSON.stringify(rows[0] ?? null), {
    status: 200,
    headers: { ...corsHeaders, 'Content-Type': 'application/json' },
  })
}

async function handleSaveProfile(req: Request, corsHeaders: Record<string, string>): Promise<Response> {
  let body: Record<string, unknown>
  try {
    body = await req.json()
  } catch {
    return new Response(JSON.stringify({ error: 'Invalid JSON body' }), {
      status: 400,
      headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  if (!body.patreon_id) {
    return new Response(JSON.stringify({ error: 'Missing patreon_id' }), {
      status: 400,
      headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  const supabaseUrl = Deno.env.get('SUPABASE_URL')
  const serviceKey = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')
  if (!supabaseUrl || !serviceKey) {
    return new Response(
      JSON.stringify({ error: 'Server configuration error', message: 'Supabase service credentials not configured' }),
      { status: 500, headers: { ...corsHeaders, 'Content-Type': 'application/json' } },
    )
  }

  const response = await fetch(`${supabaseUrl}/rest/v1/player_profiles`, {
    method: 'POST',
    headers: {
      apikey: serviceKey,
      Authorization: `Bearer ${serviceKey}`,
      'Content-Type': 'application/json',
      Prefer: 'resolution=merge-duplicates,return=minimal',
    },
    body: JSON.stringify(body),
  })

  if (!response.ok) {
    return new Response(await response.text(), {
      status: response.status,
      headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }

  return new Response(null, { status: 204, headers: corsHeaders })
}

// ============================================================================
// Main Handler
// ============================================================================

// ============================================================================
// Authorization Helper
// ============================================================================

function verifyAnonKey(authHeader: string | null, supabaseAnonKey: string | undefined): boolean {
  if (!authHeader || !supabaseAnonKey) {
    return false
  }

  // Remove "Bearer " prefix if present
  const token = authHeader.startsWith('Bearer ') ? authHeader.substring(7) : authHeader

  // Compare with SUPABASE_ANON_KEY
  return token === supabaseAnonKey
}

serve(async (req) => {
  const origin = req.headers.get('origin')
  const url = new URL(req.url)
  const authHeader = req.headers.get('authorization')

  // Get SUPABASE_ANON_KEY from environment (set in Supabase Dashboard)
  const supabaseAnonKey = Deno.env.get('SUPABASE_ANON_KEY')

  // CRITICAL DEBUG: Log everything at the start
  console.log(`[Main] ========================================`)
  console.log(`[Main] NEW REQUEST RECEIVED`)
  console.log(`[Main] Method: ${req.method}`)
  console.log(`[Main] URL: ${req.url}`)
  console.log(`[Main] Pathname: ${url.pathname}`)
  console.log(`[Main] Search: ${url.search}`)
  console.log(`[Main] Origin: ${origin || 'none'}`)
  console.log(`[Main] Authorization: ${authHeader ? 'present' : 'missing'}`)
  console.log(`[Main] SUPABASE_ANON_KEY configured: ${supabaseAnonKey ? 'yes' : 'no'}`)

  // Get action from query parameter (needed for redirect check)
  // Note: Supabase Edge Functions don't support path-based routing,
  // so we use query parameters for routing
  const action = url.searchParams.get('action')

  // Security: GET requests are public (OAuth callbacks from Patreon), EXCEPT action=get-profile
  // which reads player_profiles rows and must require SUPABASE_ANON_KEY like the POST routes.
  // POST requests require valid SUPABASE_ANON_KEY for security (except redirect)
  if (req.method === 'GET' && action !== 'get-profile') {
    // GET requests: Always allow (public OAuth callbacks)
    // No authorization required for OAuth redirects
    console.log(`[Main] ✅ GET request allowed (public OAuth callback)`)
  } else if (req.method === 'GET' || req.method === 'POST') {
    // Check if this is a redirect request (OAuth callback with code/state params)
    // Redirect requests don't require SUPABASE_ANON_KEY
    const hasOAuthParams = url.searchParams.has('code') || url.searchParams.has('state')
    const isRedirectRequest = hasOAuthParams && (action === 'redirect' || !action)

    if (isRedirectRequest) {
      // Redirect requests are public (OAuth callbacks from Patreon)
      console.log(`[Main] ✅ POST request with OAuth params - treating as redirect (no auth required)`)
    } else {
      // POST requests for token-exchange and proxy: CRITICAL - Require valid SUPABASE_ANON_KEY
      // Since "Verify JWT" is OFF, we must enforce authorization here
      if (!supabaseAnonKey) {
        console.error(`[Main] ❌ SECURITY: SUPABASE_ANON_KEY not configured!`)
        return new Response(
          JSON.stringify({
            error: 'Server configuration error',
            message: 'SUPABASE_ANON_KEY not configured. Please set it in Supabase Dashboard → Edge Functions → Settings.'
          }),
          {
            headers: {
              ...getCorsHeaders(origin),
              'Content-Type': 'application/json'
            },
            status: 500
          }
        )
      }

      if (!authHeader) {
        console.error(`[Main] ❌ SECURITY: POST request without authorization header`)
        return new Response(
          JSON.stringify({
            error: 'Unauthorized',
            message: 'POST requests require Authorization header with SUPABASE_ANON_KEY'
          }),
          {
            headers: {
              ...getCorsHeaders(origin),
              'Content-Type': 'application/json'
            },
            status: 401
          }
        )
      }

      const isValidAnonKey = verifyAnonKey(authHeader, supabaseAnonKey)
      if (!isValidAnonKey) {
        console.error(`[Main] ❌ SECURITY: POST request with invalid anon key`)
        return new Response(
          JSON.stringify({
            error: 'Unauthorized',
            message: 'Invalid authorization. Please provide valid SUPABASE_ANON_KEY in Authorization header.'
          }),
          {
            headers: {
              ...getCorsHeaders(origin),
              'Content-Type': 'application/json'
            },
            status: 401
          }
        )
      }

      console.log(`[Main] ✅ POST request authorized with valid anon key`)
    }
  }

  console.log(`[Main] Action parameter: ${action || 'none (default to redirect)'}`)

  // Determine route based on action query param or default behavior
  // Default: GET requests go to redirect, POST requests need explicit action
  let route = null
  if (action === 'token-exchange') {
    route = 'token-exchange'
    console.log(`[Main] Route determined: token-exchange`)
  } else if (action === 'proxy') {
    route = 'proxy'
    console.log(`[Main] Route determined: proxy`)
  } else if (action === 'get-profile') {
    route = 'get-profile'
    console.log(`[Main] Route determined: get-profile`)
  } else if (action === 'save-profile') {
    route = 'save-profile'
    console.log(`[Main] Route determined: save-profile`)
  } else if (action === 'create-handoff') {
    route = 'create-handoff'
    console.log(`[Main] Route determined: create-handoff`)
  } else if (action === 'redeem-handoff') {
    route = 'redeem-handoff'
    console.log(`[Main] Route determined: redeem-handoff`)
  } else if (action === 'supabase-session') {
    route = 'supabase-session'
    console.log(`[Main] Route determined: supabase-session`)
  } else if (action === 'token-refresh') {
    route = 'token-refresh'
    console.log(`[Main] Route determined: token-refresh`)
  } else {
    // Default to redirect for GET requests or when no action specified
    // This handles OAuth callbacks (GET with code and state params)
    route = 'redirect'
    console.log(`[Main] Route determined: redirect (default - action: ${action || 'none'}, method: ${req.method})`)
  }

  // Handle preflight OPTIONS request
  if (req.method === 'OPTIONS') {
    return new Response('ok', { headers: getCorsHeaders(origin) })
  }

  // Route to appropriate handler
  // GET requests: Always go to redirect handler (OAuth callbacks)
  if (req.method === 'GET') {
    console.log(`[Main] Processing GET request, route: ${route}`)
    if (route === 'redirect') {
      // Redirect to external OAuth redirect page with all query parameters
      const externalRedirectUrl = buildExternalRedirectUrl(url)
      console.log(`[Main] ✅ Redirecting (302) to external host: ${externalRedirectUrl}`)

      const corsHeaders = getCorsHeaders(origin, 'GET, OPTIONS')
      return new Response(null, {
        status: 302,
        headers: {
          ...corsHeaders,
          'Location': externalRedirectUrl
        }
      })
    } else if (route === 'get-profile') {
      console.log(`[Main] ✅ Routing GET request to get-profile handler`)
      return handleGetProfile(url, getCorsHeaders(origin, 'GET, OPTIONS'))
    } else {
      // GET request with action=token-exchange or action=proxy is invalid
      console.error(`[Main] ❌ ERROR: GET request with invalid action: ${action}`)
      return new Response(
        JSON.stringify({
          error: 'Invalid request',
          message: `GET requests cannot use action=${action}. GET requests are for OAuth redirects or get-profile only.`
        }),
        {
          headers: {
            ...getCorsHeaders(origin),
            'Content-Type': 'application/json'
          },
          status: 400
        }
      )
    }
  }

  // POST requests: Route based on action
  // NOTE: Some Supabase routing may convert GET to POST, so we handle redirect for POST too
  if (req.method === 'POST') {
    console.log(`[Main] Processing POST request, route: ${route}`)

    // Special case: POST request with code/state params (OAuth callback) should go to redirect handler
    // This handles cases where Supabase's internal routing converts GET to POST
    const hasOAuthParams = url.searchParams.has('code') || url.searchParams.has('state')
    if (hasOAuthParams && route === 'redirect') {
      const externalRedirectUrl = buildExternalRedirectUrl(url)
      console.log(`[Main] ✅ Redirecting (302) POST to external host: ${externalRedirectUrl}`)

      const corsHeaders = getCorsHeaders(origin, 'GET, POST, OPTIONS')
      return new Response(null, {
        status: 302,
        headers: {
          ...corsHeaders,
          'Location': externalRedirectUrl
        }
      })
    }

    if (route === 'token-exchange') {
      // Token exchange
      console.log(`[Main] ✅ Routing POST request to token-exchange handler`)
      return handleTokenExchange(req, getCorsHeaders(origin, 'POST, OPTIONS'), supabaseAnonKey)
    } else if (route === 'save-profile') {
      console.log(`[Main] ✅ Routing POST request to save-profile handler`)
      return handleSaveProfile(req, getCorsHeaders(origin, 'POST, OPTIONS'))
    } else if (route === 'proxy') {
      // API proxy
      console.log(`[Main] ✅ Routing POST request to proxy handler`)
      return handleProxy(req, getCorsHeaders(origin, 'POST, OPTIONS'), supabaseAnonKey)
    } else if (route === 'create-handoff') {
      console.log(`[Main] ✅ Routing POST request to create-handoff handler`)
      return handleCreateHandoff(req, getCorsHeaders(origin, 'POST, OPTIONS'), supabaseAnonKey)
    } else if (route === 'redeem-handoff') {
      console.log(`[Main] ✅ Routing POST request to redeem-handoff handler`)
      return handleRedeemHandoff(req, getCorsHeaders(origin, 'POST, OPTIONS'), supabaseAnonKey)
    } else if (route === 'supabase-session') {
      return handleSupabaseSession(req, getCorsHeaders(origin, 'POST, OPTIONS'), supabaseAnonKey)
    } else if (route === 'token-refresh') {
      console.log(`[Main] ✅ Routing POST request to token-refresh handler`)
      return handleTokenRefresh(req, getCorsHeaders(origin, 'POST, OPTIONS'), supabaseAnonKey)
    } else {
      // POST request without action or with invalid action
      console.error(`[Main] ❌ ERROR: POST request without valid action - action: ${action || 'none'}`)
      return new Response(
        JSON.stringify({
          error: 'Invalid request',
          message: `POST requests require ?action=token-exchange, ?action=proxy, ?action=create-handoff, ?action=redeem-handoff, ?action=token-refresh, or ?action=supabase-session. Received: ${action || 'none'}`
        }),
        {
          headers: {
            ...getCorsHeaders(origin),
            'Content-Type': 'application/json'
          },
          status: 400
        }
      )
    }
  }

  // Unknown method (should not happen after OPTIONS check)
  console.error(`[Main] ❌ ERROR: Unknown method - method: ${req.method}, route: ${route}, action: ${action || 'none'}`)
  return new Response(
    JSON.stringify({
      error: 'Method not allowed',
      message: `Method ${req.method} is not supported. Use GET for redirects or POST with ?action=token-exchange|proxy`
    }),
    {
      headers: {
        ...getCorsHeaders(origin),
        'Content-Type': 'application/json'
      },
      status: 405
    }
  )
})

