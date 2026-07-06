# Handoff: Pulsoid ProtoFlux (no-mod) heart rate — what exists, what's broken, what to build

This document is a complete spec for finishing the in-game-only (ProtoFlux) Pulsoid
heart rate option, written so a fresh session or builder can pick it up without any
prior context. The C# mod in this repo works and is released (v1.1.3); this covers
only the ProtoFlux alternative in `protoflux/`.

---

## 1. Goal

A single spawnable Resonite item that, with **no mod installed**:

1. Accepts a Pulsoid **widget URL** (`https://pulsoid.net/widget/view/<widget-id>`),
   a bare **widget ID** (GUID), or a **raw access token** (GUID) from the user.
2. Resolves the widget URL/ID into an access token via Pulsoid's public RPC
   (no login, no paid plan).
3. Connects to Pulsoid's free real-time websocket with that token.
4. Parses each message and writes the heart rate to a dynamic variable
   `User/Pulsoid.HeartRate` (int) that avatar systems can read.
5. Fails **visibly** (status text) instead of silently, and can be re-triggered
   without respawning the item.

## 2. Pulsoid protocol reference (verified against the working C# mod)

These exact values are implemented and known-working in
`ResoniteHeartRate/PulsoidTokenResolver.cs` and
`ResoniteHeartRate/PulsoidWebSocket.cs` — treat those two files as the reference
implementation.

### 2.1 Widget ID → token (JSON-RPC)

```
POST https://pulsoid.net/v1/api/public/rpc
Content-Type: application/json
x-rpc-method: getWidget          <- the C# mod sends this; see §5 "Open questions"

{"jsonrpc":"2.0","method":"getWidget","params":{"widgetId":"<widget-id>"},"id":"1"}
```

Success response contains `"token":"<access-token>"` (flat string field inside
`result`). A wrong/unknown widget ID returns a JSON-RPC error with **no** `token`
key — that is also the signal that a bare GUID the user pasted was actually a raw
token, not a widget ID (they share the GUID shape).

### 2.2 Real-time websocket

```
wss://dev.pulsoid.net/api/v1/data/real_time?access_token=<token>&response_mode=legacy_json
```

`legacy_json` text frames look like:

```json
{"timestamp":1782694167485,"data":{"heartRate":100}}
```

Key is camelCase `heartRate`, and it is the last field before `}}`. A bad token
gets the connection rejected (HTTP 401 during the upgrade).

Notes from the C# implementation:
- Trim whitespace off the token before building the URL (pasted whitespace
  URL-encodes into the token and causes 401).
- Don't reconnect more often than every ~15 s — hammering the endpoint gets the
  token rate-limited (401s on a valid token).
- Widget-derived tokens can go stale; on repeated connect failure, re-run the
  RPC resolution rather than retrying the old token forever.

## 3. What exists now

- `protoflux/Pulsoid_Heart_Rate.resonitepackage` — the item (Resonite save format).
- `protoflux/Pulsoid_Heart_Rate_decoded.json` — decoded copy for inspection.

### 3.1 Item structure

- Root slot `Pulsoid Heart Rate (User/Pulsoid.HeartRate)` with:
  - `DynamicVariableSpace` named `User`
  - `DynamicValueVariable<int>` named `Pulsoid.HeartRate`
- Child slot `Pulsoid Websocket` with a `WebsocketClient` component
  (URL preset to `wss://dev.pulsoid.net/api/v1/data/real_time` **without** a
  token; `ConnectRetryInterval` = 10).
- A ProtoFlux slot tree (`ProtoFlux` / `Moduprint.ProtoFlux`) with the graph below.

### 3.2 Current graph flow (as wired)

```
OnStart
  └─> POST_String  "POST getWidget"
        URL:    https://pulsoid.net/v1/api/public/rpc
        Body:   '{"jsonrpc":"2.0","method":"getWidget","params":{"widgetId":"' + <id> + '"}...'
                where <id> = Substring(input, IndexOf-last-'/' + 1)   [Max with 0 handles no-slash input]
        MediaType: application/json
        OnSent: (unwired)  OnError: (unwired)  OnDenied: (unwired)
        OnResponse ─> RequestHostAccessUrl
                        Host:  the built wss:// URI
                        Scope: Websocket
                        Reason: "Host Access lets Resonite connect to Pulsoid (dev.pulsoid.net)..."
                        OnDenied/OnIgnored: (unwired)
                        OnGranted ─> WebsocketConnect
                                       Client: the WebsocketClient component
                                       URL:    wss prefix + token + "&response_mode=legacy_json"
                                       HandlingUser: LocalUser

token extraction (data-flow, feeding the wss URL):
  IndexOfString(response, '"token":"') + len('"token":"')  = start
  IndexOfString(response, '"', from start)                 = end
  Substring(response, start, end - start)                  = token

message handling:
  WebsocketTextMessageReceiver (bound to the WebsocketClient)
    └─> data-flow parse:
          start = IndexOfString(msg, '"heartRate":') + 12
          end   = IndexOfString(msg, '}', from start)
          hr    = Parse_Int(Substring(msg, start, end - start))
        Parse_Int success ─> If ─> WriteDynamicValueVariable<int>
                                     Target: root slot
                                     Path:   "User/Pulsoid.HeartRate"
```

The string parsing, URL construction, and write logic are all **correct** — reuse
them as-is. The defects are in triggering, permissions, and error paths.

## 4. Defects (why the last test showed nothing)

Ranked by certainty/impact:

1. **One-shot trigger with a placeholder baked in.** `OnStart` is the only
   trigger, and the shipped input value is `PASTE_YOUR_PULSOID_WIDGET_URL_OR_ID`.
   On spawn the graph runs once with the placeholder and dead-ends. Pasting the
   real URL afterwards re-runs nothing. The natural test flow
   (spawn → paste → wait) is guaranteed to do nothing.

2. **No HTTP host-access request before the POST.** The only
   `RequestHostAccessUrl` is Websocket-scoped for `dev.pulsoid.net` and sits
   *after* the POST response. HTTP and Websocket are separate per-host consent
   scopes in Resonite. If `https://pulsoid.net` (HTTP scope) isn't already
   allowed on the client, the POST is denied — and since `OnDenied`/`OnError`
   are unwired, the chain stops with zero feedback.

3. **No error/denied feedback anywhere.** POST `OnError`/`OnDenied`, host-access
   `OnDenied`/`OnIgnored` — all unwired. Every failure mode is silent.

4. **No raw-token path.** A pasted raw access token goes through the widget RPC,
   which errors, and there is no fallback to use the GUID directly (the C# mod
   handles this case).

5. **No re-resolution on reconnect.** The `WebsocketClient` retries every 10 s
   with the same tokened URL. If the widget token expires, it 401s forever.
   (Acceptable for v1, but note it.)

6. **Self-contained `User` dynvar space.** The item root carries its own
   `DynamicVariableSpace "User"`, so `User/Pulsoid.HeartRate` is only visible to
   readers **under the item's hierarchy**. Avatar systems reading the variable
   from the user root will not see it. See §6 for integration options.

## 5. Open questions to resolve while building

- **Is the `x-rpc-method: getWidget` header required?** The C# mod sends it
  (captured from the live widget page), but ProtoFlux's `POST_String` has no
  header input, so the graph cannot send it. Test once with curl:

  ```bash
  curl -X POST 'https://pulsoid.net/v1/api/public/rpc' \
    -H 'Content-Type: application/json' \
    -d '{"jsonrpc":"2.0","method":"getWidget","params":{"widgetId":"<real-widget-id>"},"id":"1"}'
  ```

  If it returns the token without the header, the graph's RPC approach is fine.
  If the header is required, the RPC **cannot be done from ProtoFlux** — drop the
  widget-URL convenience and require the raw token (the §6 token-direct path
  becomes the only path). (Could not be verified from the authoring sandbox —
  outbound proxy blocked pulsoid.net.)

- **Does a denied HTTP request prompt the user automatically?** Wiki guidance is
  to use `RequestHostAccess` explicitly and handle denial gracefully; do not rely
  on an automatic prompt. Build the explicit request in regardless.

## 6. Build spec (the fix)

### 6.1 Trigger and control

- Keep `OnStart` (auto-connect for a saved, pre-configured copy), but **also**
  add a user-facing **"Connect" button** (`TouchButton`/UIX button →
  `ButtonEvents` → same chain) and optionally a `DynamicImpulseReceiver` with tag
  `Pulsoid.Connect` so other gadgets can re-trigger it.
- Both triggers converge (ContinuationRelay) into one chain.

### 6.2 The chain (impulse order)

```
[Connect trigger]
  └─> If (input contains "/widget/view/" OR RPC path enabled)
        TRUE  (widget URL/ID path):
          └─> RequestHostAccessUrl
                Host:  https://pulsoid.net        <- STRING/URI CONSTANT, HTTP SCOPE
                Scope: HTTP
                Reason: "Look up your Pulsoid widget token (one small request to pulsoid.net)."
                OnDenied/OnIgnored ─> [status = "Denied access to pulsoid.net - allow it in Settings > Host Access"]
                OnGranted ─> POST_String (existing node, unchanged body/URL)
                       OnError  ─> [status = "Pulsoid RPC failed - check your widget URL"]
                       OnDenied ─> [status = "HTTP access denied"]
                       OnResponse ─> If (IndexOfString(response, '"token":"') > -1)
                             FALSE ─> [status = "No token in response - is that a widget URL/ID?"]
                                       └─> fall through to token-direct path with the raw input
                             TRUE  ─> continue to websocket stage with extracted token
        FALSE (token-direct path):
          └─> use trimmed input as the token, continue to websocket stage

[websocket stage]  (shared)
  └─> RequestHostAccessUrl
        Host:  the built wss:// URI (existing wiring)
        Scope: Websocket
        OnDenied/OnIgnored ─> [status = "Denied access to dev.pulsoid.net"]
        OnGranted ─> WebsocketConnect (existing wiring: client, tokened URL, LocalUser)
                       └─> [status = "Connecting..."]
```

Implementation notes:

- "input contains `/widget/view/`" = `IndexOfString(input, "/widget/view/") > -1`.
  A bare GUID can't be distinguished from a raw token up front — mirror the C#
  behaviour: try the RPC first, fall back to token-direct when no `"token":"` is
  found in the response.
- Trim the input (`TrimString`) before every use.
- `[status = "..."]` = write a string to a `DynamicValueVariable<string>`
  `Pulsoid.Status` on the root slot, displayed on the item with a `<Text>` UIX or
  TextRenderer driven by the variable. Also set status from
  `WebsocketClient.IsConnected` (drive a bool → status text) so the user can see
  connected/disconnected state.
- Keep the existing message-parse subgraph and
  `WriteDynamicValueVariable<int>("User/Pulsoid.HeartRate")` untouched.
- Optionally write `0` on `WebsocketClosed` so displays don't freeze on the last
  value.

### 6.3 Dynamic variable integration (pick one, document it on the item)

- **Option A (self-contained, current design):** keep the item's own
  `DynamicVariableSpace "User"`. Anything reading the variable must be inside the
  item's hierarchy. Good for a bundled display; invisible to avatar systems.
- **Option B (avatar integration):** instruct the user to parent the item under
  their avatar/user root and **delete the item's own `DynamicVariableSpace`**, so
  the write binds to the user root's real `User` space and
  `User/Pulsoid.HeartRate` becomes readable avatar-wide (matches how the C# mod
  exposes its variable).
- Include both instructions on a small info panel on the item.

### 6.4 Package hygiene

- Ship the input field **empty** (not `PASTE_YOUR_PULSOID_WIDGET_URL_OR_ID`), or
  keep the placeholder but rely on the Connect button as the primary trigger.
- Clear the `WebsocketClient`'s preset URL (it should only ever get the tokened
  URL from `WebsocketConnect`), or leave it — with no `HandlingUser` it won't
  connect on its own, but a clean null URL avoids confusion.
- Re-export the `.resonitepackage` and regenerate the decoded JSON for the repo.

## 7. Acceptance test checklist

On a client that has **never** granted Pulsoid host access (or after revoking
`pulsoid.net` and `dev.pulsoid.net` in Settings → Host Access):

1. Spawn item → status shows idle/instructions, nothing crashes, no silent POST
   of a placeholder.
2. Paste widget **URL** → press Connect → HTTP access prompt appears → allow →
   Websocket access prompt appears → allow → status "Connecting..." → live BPM
   appears in `User/Pulsoid.HeartRate` within a few seconds (Pulsoid app must be
   streaming).
3. Repeat from scratch with a bare **widget ID** — same result.
4. Repeat with a **raw access token** — must connect without a successful RPC.
5. Deny the HTTP prompt → status says so explicitly.
6. Paste garbage → status reports RPC/parse failure, no crash.
7. Kill and restart the Pulsoid app mid-session → value resumes (client retry),
   or at minimum status reflects the disconnect.
8. Option B integration: parent under avatar, delete item's `User` space, confirm
   an avatar system elsewhere on the user root reads `User/Pulsoid.HeartRate`.

## 8. Constants quick-reference

| Item | Value |
|---|---|
| RPC endpoint | `https://pulsoid.net/v1/api/public/rpc` |
| RPC body prefix | `{"jsonrpc":"2.0","method":"getWidget","params":{"widgetId":"` |
| RPC body suffix | `"},"id":"1"}` |
| RPC media type | `application/json` |
| Token search key | `"token":"` |
| WSS prefix | `wss://dev.pulsoid.net/api/v1/data/real_time?access_token=` |
| WSS suffix | `&response_mode=legacy_json` |
| Message key | `"heartRate":` (value ends at next `}`) |
| Output variable | `User/Pulsoid.HeartRate` (int) |
| Status variable (new) | `User/Pulsoid.Status` (string) |
| Host access needed | `https://pulsoid.net` (HTTP) + `wss://dev.pulsoid.net` (Websocket) |
