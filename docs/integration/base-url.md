# Runtime mount prefix (`BASE_URL`)

One unchanged image runs at root or behind a path-preserving reverse proxy:

```yaml
services:
  mahjong:
    image: ghcr.io/long2know/mahjong-web:latest
    environment:
      BASE_URL: /mahjong
      # Only when this is the actual immediate trusted proxy peer:
      ReverseProxy__KnownProxies__0: 172.20.0.10
```

The registry tag above is the new canonical release target, not a claim that a
particular candidate has already been promoted. Keep existing data/signing
configuration and volumes. The repository Compose file passes `BASE_URL` and
optional `TRUSTED_PROXY_IP` into the **runtime environment**, not build args.

| Runtime value | Public entry |
|---|---|
| unset, empty or `/` | UI directly at `/` |
| `/mahjong` or `/mahjong/` | UI directly at `/mahjong/`; `/mahjong` only gains its trailing slash |
| `/games/mahjong` | UI directly at `/games/mahjong/` |
| `/My%20Game` or literal `/My Game` | UI directly at `/My%20Game/` |

For **https://games.long2know.com/mahjong/** use **`BASE_URL=/mahjong`**.
The canonical entry returns the actual game HTML with no suffix redirect.
Bookmarks/query parameters, module/artwork URLs, worker scope and navigation use
that same mount root; the raw relay is `/mahjong/ws`. Retired `/autotable/`
paths return 404 rather than serving a second copy of the app.

This setting is a site path, **not an absolute public-origin URL** and not Vite's
build-time `BASE_URL`. It normalizes one trailing slash. Invalid relative/full
URLs, double slashes, dot/traversal segments, controls, query/fragment delimiters,
backslashes, encoded separators, malformed or nested escapes fail startup with
a clear configuration error. Ordinary escaped spaces/Unicode/unreserved path
characters are supported. Existing separate full-URL email/OAuth options are
not repurposed.

All application APIs, raw WebSockets, SignalR negotiation/reconnect, guest/auth
cookies, OAuth callback links, livestream aliases/returned playlist URLs,
public navigation and static assets live under
the prefix.
Wrong-root APIs are rejected, not silently served as a second installation.
The unprefixed `/health` remains available for the internal Docker HEALTHCHECK;
the prefixed `/mahjong/health` also works.

Browser request/navigation resolution is centralized in `src/app-url.ts`.
Absolute/external URLs are not prefixed. Static imports/artwork, web manifest
and service-worker URLs remain relative to the application. Worker caches are
mount-namespaced; private API/hub traffic is network-only, and one mounted app
cannot erase another mount's caches. No served hashed JavaScript, HTML or
`web-build.json` is rewritten for a runtime prefix.

## nginx: preserve the entire path

Put the `map` in nginx's `http` context. This example assumes TLS terminates at
this proxy and its immediate IP is explicitly listed in `ReverseProxy:KnownProxies`.
Use an exact trusted address, not global forwarded-header trust.

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    '' close;
}

server {
    listen 443 ssl;
    server_name games.example.com;
    # Configure the operator's certificate/key normally.

    location /mahjong {
        proxy_pass http://mahjong:8080; # NO trailing slash/URI: preserve /mahjong/...
        proxy_http_version 1.1;
        proxy_set_header Host $http_host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_read_timeout 60s;
    }
}
```

Do not use `proxy_pass http://mahjong:8080/` here: that changes the prefix
contract. nginx must overwrite forwarded headers, and the app accepts only
one hop from configured known peers. Without a trusted peer configuration,
forwarded scheme/client headers are deliberately ignored. Set the normal
`AllowedHosts` configuration for your public hostname; public URLs derive
from the preserved Host header and trusted scheme. No unrestricted
`ASPNETCORE_FORWARDEDHEADERS_ENABLED` override is required.

The middleware order follows ASP.NET's `UsePathBase` **before** `UseRouting`.
The example follows nginx's documented URI-preserving `proxy_pass` behavior
and explicit WebSocket Upgrade forwarding.

## Evidence

`tests/migration/container_proxy_smoke.py` runs the **same canonical image**
at root, `/mahjong`, `/games/mahjong` and an escaped-space prefix behind a pinned real
nginx TLS proxy, with synthetic state and explicit known peer IPs. It checks
unchanged build-manifest bytes, strict CSP, correct HTTPS cookies, actual
browser drawing/discard, raw relay WebSocket, SignalR negotiation/reconnect,
worker scope/cache paths, direct 200 mount-root HTML, query-preserving slash
canonicalization and absence of wrong-root or legacy-suffix app requests. Synthetic test
certificates/keys are deleted and not uploaded. No operator credentials,
production persistence or registry publication is part of that check.

Native direct-path-base checks are recorded separately and are not called
nginx proof. External OAuth provider round trips and optional unavailable
streaming features require their own operator configuration; tests inspect
real callback/challenge URLs without using real provider credentials.
