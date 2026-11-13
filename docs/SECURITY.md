# Security and abuse

This service indexes public metadata. It stores no files, hosts no content and
cannot remove anything from the BitTorrent network. That shapes the threat model.

## Attack surface

| Surface | Exposure | Mitigation |
|---|---|---|
| public HTTP | internet, via reverse proxy | per-IP rate limit, body cap, strict CSP, no auth, no state |
| admin HTTP | LAN / loopback by default | separate port, token → signed cookie, private-only check |
| DHT UDP | internet | parser is strict and bounded, no state growth per packet |
| outbound TCP | peers | only to peers of a hash we already harvested |
| disk | index, logs | append-only, size caps, no user-uploaded files |

There is **no upload endpoint, no user account, no session store, no database**.
The most a remote attacker can do is send requests and KRPC packets.

## Admin board

* Binds to `server.adminBind` (default `127.0.0.1`), a different port from the
  public board. `/api/admin/` simply does not exist on the public port.
* `server.adminPrivateOnly=true` refuses non-private client addresses even if the
  port is forwarded — a second net behind a router mistake.
* The token is generated on first start (32 hex chars), stored in
  `config.ini [security] adminToken`, and printed to the console **only**, never
  to the log file.
* Login exchanges the token for an `HttpOnly`, `SameSite=Lax` cookie whose value
  is `HMAC-SHA1(serverSecret, "admin|" + adminToken)`. The token itself is never
  stored in the browser.
* Comparison is constant-time; failed logins are rate limited (10/min/IP) and
  logged with the source address.
* `security.allowQueryToken` exists for debugging and is off by default: a token
  in a URL leaks into history, logs and `Referer`.

Rotate the token by editing `config.ini` and restarting; every existing cookie
becomes invalid because it is derived from the token.

## Public board

* Per-IP fixed-window limiter (`server.requestsPerMinute`, default 600/min).
  Over the limit → `429` with `Retry-After: 60`.
* `X-Forwarded-For` is honoured **only** when the direct peer matches
  `server.trustedProxies`; otherwise the socket address is used. This is what stops
  a client from spoofing its IP through a header.
* Request body cap (`server.maxBodyBytes`, 256 KB). The public API is read-only, so
  bodies are ignored anyway.
* Header and request-line limits are enforced while parsing (8 KB line, 64 KB
  headers, 128 headers).
* Path handling resolves the real path and refuses anything outside `public/`.
  `..%2f` and encoded separators cannot escape.
* Static files are served from a whitelist of prefixes (`/assets/`, `/favicon.svg`,
  `/manifest.webmanifest`, `/og.svg`), never by generic file mapping.

### Headers

```
Content-Security-Policy: default-src 'self'; img-src 'self' data:;
  style-src 'self' 'unsafe-inline'; script-src 'self'; object-src 'none';
  base-uri 'none'; form-action 'self'; frame-ancestors 'none'
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
Referrer-Policy: no-referrer
Permissions-Policy: geolocation=(), microphone=(), camera=()
```

`style-src 'unsafe-inline'` is required by the generated palette and the inline
progress bars; there is no inline script anywhere. HSTS is opt-in
(`server.hsts=true`) because it must only be sent behind real TLS.

### XSS

Every value that reaches HTML goes through `Text.HtmlEscape`, including torrent
names and file paths — which are attacker-controlled by definition. The JSON API
escapes control characters. There is no template engine and no string
concatenation of unescaped input into markup.

## DHT hardening

* The bencode decoder enforces nesting depth (32), string bounds and integer size,
  and throws on anything malformed. A hostile packet is dropped, not retried.
* Packet size is bounded by the UDP receive buffer (8 KB); compact node/peer lists
  are validated element by element.
* Transaction ids are 2 bytes and swept after 60 s, so the pending table cannot
  grow without bound.
* The token we hand out for `announce_peer` is an HMAC over the sender IP; we do
  not accept unverified announces as facts (we only index the hash).
* Rate limiting on new hashes (`dht.maxInfohashesPerMin`) is the main defence
  against a flood that would fill the disk.
* Metadata is verified with SHA-1 against the info hash **before** it is parsed or
  stored. Poisoned metadata is discarded.
* Peers in private address ranges are never dialled (`NetUtil.IsPrivate`), so a
  malicious peer cannot make the server probe the LAN.

## Logging

* Logs never contain the admin token, `serverSecret` or request bodies.
* API tracing records method, path, status, duration and body size only.
* Log files roll by day and size and are pruned after `logging.retentionDays`.
* The admin log panel reads the same in-memory ring buffer, capped at
  `limits.logRingSize` entries.

## Content policy

An indexer cannot delete a torrent, but it can stop showing it:

```bash
# data/blocklist.txt
# one 40-char info hash per line, optional reason after a tab
0123456789abcdef0123456789abcdef01234567    DMCA - reported by rights holder
fedcba9876543210fedcba9876543210fedcba98
```

Blocked hashes are:

* filtered out of search results and `/api/*`;
* filtered from `/t/<hash>` (404);
* refused at harvest time, so no metadata is fetched for them;
* counted in `crawler.blocked` on the stats page.

Reload without a restart: admin board → *Reload blocklist*, or `POST
/api/reload`, or the `reload` console command.

`[site] abuseEmail` shows the contact address on `/abuse`; leave it empty and the
page explains that this instance has no contact configured.

## Deployment checklist

- [ ] `server.adminBind=127.0.0.1` (or a LAN address) and `adminPrivateOnly=true`
- [ ] TLS terminated at the proxy, `server.hsts=true` only then
- [ ] `server.requestsPerMinute` tuned to real traffic
- [ ] `data/blocklist.txt` present if you accept abuse reports
- [ ] `config.ini` not readable by other users on the box
- [ ] logs rotated and shipped somewhere you actually look
- [ ] firewall script run: only the proxy port and UDP 6881 reachable
