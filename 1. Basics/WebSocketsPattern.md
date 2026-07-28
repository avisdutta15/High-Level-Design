# WebSockets Pattern

## Architecture Overview

```
┌──────────┐  WS upgrade + bidirectional frames  ┌──────────────┐  Subscribe/Publish  ┌───────────┐
│  Client  │ ◄══════════════════════════════════► │  WS Server   │ ◄──────────────────► │   Redis   │
│ (Browser)│  Full-duplex on single TCP socket    │              │     Pub/Sub          │  Pub/Sub  │
└──────────┘                                      └──────────────┘                      └───────────┘
                                                         │
                                                         ▼
                                                  ┌──────────────┐
                                                  │   Database   │
                                                  │ (state/meta) │
                                                  └──────────────┘
```

Unlike SSE (server → client only), WebSockets provide **full-duplex** communication. Both client and server can send messages at any time over a single persistent TCP connection.

---

## 1. Introduction to WebSockets

### What Problem Do WebSockets Solve?

HTTP is request-response: the client asks, the server answers, the connection effectively resets. For real-time applications (chat, gaming, live trading, collaborative editing), you need the server to push data to the client **without the client asking first**, AND the client to send data without opening a new connection each time.

```
              HTTP (Half-Duplex, Request-Response)
═══════════════════════════════════════════════════════
Client                          Server
  │── GET /messages ──────────────►│
  │◄── 200 OK [{...}] ────────────│  (connection closes)
  │                                │
  │── GET /messages?after=5 ──────►│  (must poll again)
  │◄── 200 OK [{...}] ────────────│  (connection closes)
  │                                │
  │  Wasteful: opens new TCP       │
  │  connection every time.        │
  │  Latency: must wait for next   │
  │  poll interval.                │


              WebSocket (Full-Duplex, Persistent)
═══════════════════════════════════════════════════════
Client                          Server
  │── HTTP Upgrade ───────────────►│
  │◄── 101 Switching Protocols ────│  (upgraded to WS)
  │                                │
  │◄══ frame: new message ════════►│  (server pushes anytime)
  │◄══ frame: typing indicator ═══►│  (client sends anytime)
  │◄══ frame: new message ════════►│
  │◄══ frame: user left ══════════►│
  │                                │
  │  Single TCP connection.        │
  │  Both sides send whenever      │
  │  they have data.               │
```

### The WebSocket Handshake

WebSocket connections start as HTTP and then **upgrade** to the WebSocket protocol:

```
Client                                              Server
  │                                                    │
  │  ① HTTP GET with Upgrade headers:                  │
  │  ─────────────────────────────────────             │
  │  GET /chat HTTP/1.1                                │
  │  Host: api.example.com                             │
  │  Upgrade: websocket                                │
  │  Connection: Upgrade                               │
  │  Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==      │
  │  Sec-WebSocket-Version: 13                         │
  │  Origin: https://example.com                       │
  │────────────────────────────────────────────────────►│
  │                                                    │
  │  ② Server responds with 101:                       │
  │  ─────────────────────────────                     │
  │  HTTP/1.1 101 Switching Protocols                  │
  │  Upgrade: websocket                                │
  │  Connection: Upgrade                               │
  │  Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo= │
  │◄────────────────────────────────────────────────────│
  │                                                    │
  │  ③ Protocol switched — now speaking WebSocket      │
  │  ═══════════════════════════════════════════        │
  │  Binary frames, not HTTP anymore.                  │
  │  Both sides can send at any time.                  │
  │◄══════════════════════════════════════════════════►│
```

Key points:
- **`Sec-WebSocket-Key`**: Random base64 nonce from the client. The server hashes it with a magic GUID and returns it as `Sec-WebSocket-Accept` to prove it understands the WebSocket protocol.
- **After 101**: The TCP socket is no longer HTTP. It speaks the WebSocket frame protocol (2-14 byte frame headers, binary or text payloads).
- **No more HTTP overhead**: No headers, no cookies repeated on every message. Just lightweight frames.

### WebSocket Frame Structure

```
  0                   1                   2                   3
  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
 +-+-+-+-+-------+-+-------------+-------------------------------+
 |F|R|R|R| opcode|M| Payload len |    Extended payload length     |
 |I|S|S|S|  (4)  |A|     (7)     |           (16/64)              |
 |N|V|V|V|       |S|             |   (if payload len == 126/127)  |
 | |1|2|3|       |K|             |                                |
 +-+-+-+-+-------+-+-------------+-------------------------------+
 |    Masking-key (if MASK set)  |           Payload Data         |
 +-------------------------------+-------------------------------+

 Overhead: 2-14 bytes per frame (vs ~800 bytes for HTTP headers)
```

Frame types (opcodes):
- `0x1` — Text frame (UTF-8)
- `0x2` — Binary frame
- `0x8` — Connection close
- `0x9` — Ping (keepalive)
- `0xA` — Pong (response to ping)

---

## 2. WebSockets Over TLS (WSS)

### Why WSS (Not WS)

In production, you **always** use `wss://` (WebSocket Secure) instead of `ws://`. This wraps the WebSocket connection inside TLS, identical to how HTTPS wraps HTTP.

```
              UNENCRYPTED (ws://) — NEVER use in production
═══════════════════════════════════════════════════════════════
Client ──── TCP ──── [plaintext frames] ──── Server
                         ▲
                 Interceptable by:
                 - ISPs
                 - Corporate proxies
                 - Wifi sniffers
                 - MITM attacks


              ENCRYPTED (wss://) — Production standard
═══════════════════════════════════════════════════════════════
Client ──── TCP ──── TLS tunnel ──── [encrypted frames] ──── Server
                         ▲
                 Protected:
                 - Frames encrypted end-to-end
                 - Certificate validates server identity
                 - Proxies/firewalls can't inspect content
                 - Required for browsers on HTTPS pages
```

### The WSS Connection Flow

```
Client                         Load Balancer / TLS Termination        WS Server
  │                                        │                              │
  │  ① TCP 3-way handshake                 │                              │
  │────── SYN ────────────────────────────►│                              │
  │◄───── SYN-ACK ────────────────────────│                              │
  │────── ACK ────────────────────────────►│                              │
  │                                        │                              │
  │  ② TLS Handshake (within TCP)          │                              │
  │────── ClientHello ────────────────────►│                              │
  │◄───── ServerHello + Certificate ───────│                              │
  │────── Key Exchange ───────────────────►│                              │
  │◄───── Finished ────────────────────────│                              │
  │                                        │                              │
  │  ③ HTTP Upgrade (inside TLS tunnel)    │                              │
  │══════ GET /ws Upgrade: websocket ═════►│──── forward (plaintext) ────►│
  │◄═════ 101 Switching Protocols ═════════│◄─── 101 ────────────────────│
  │                                        │                              │
  │  ④ WebSocket frames (inside TLS)       │                              │
  │◄═══════════ encrypted frames ═════════►│◄═══ plaintext frames ══════►│
  │                                        │                              │
```

**TLS termination options:**
- **At the Load Balancer** (most common): LB handles TLS, forwards plaintext to WS servers. Simpler server config, TLS cert managed in one place.
- **End-to-end (passthrough)**: LB forwards encrypted TCP, WS server handles TLS. More secure internally but harder to manage.

### Certificate Considerations

| Concern | Detail |
|---------|--------|
| Certificate type | Standard TLS cert (same as HTTPS). Let's Encrypt works fine. |
| SNI support | Required if multiple domains share the same LB IP |
| TLS version | TLS 1.2 minimum, prefer TLS 1.3 (faster handshake: 1-RTT vs 2-RTT) |
| Cipher suites | Use strong ciphers (AES-256-GCM, CHACHA20-POLY1305) |
| Certificate rotation | Automated via ACM (AWS) or certbot. Connection persists through rotation — existing WS connections are not dropped. |

---

## 3. Load Balancing & API Gateway for WebSockets

### The Challenge: WebSockets Are Not Standard HTTP

Standard HTTP load balancing strategies (round-robin per request) don't work well with WebSockets because:
1. The connection is long-lived (minutes to hours)
2. The upgrade handshake is HTTP, but after that it's a different protocol
3. HTTP-only proxies/gateways may not understand WebSocket frames
4. Connection draining during deploys requires special handling

### What Kind of Load Balancer Do You Need?

```
                    LB REQUIREMENTS FOR WEBSOCKETS
═══════════════════════════════════════════════════════════════════════

  ┌─────────────────────────────────────────────────────────────────┐
  │                    REQUIRED FEATURES                              │
  │                                                                  │
  │  ✅ Layer 7 (HTTP-aware) with WebSocket upgrade support          │
  │  ✅ Long-lived connection support (idle timeout > 1 hour)        │
  │  ✅ Connection draining (graceful shutdown without dropping WS)   │
  │  ✅ Health checks that don't kill WS connections                  │
  │  ✅ TLS termination with SNI support                             │
  │  ✅ Sticky sessions (optional, but simplifies some patterns)     │
  └─────────────────────────────────────────────────────────────────┘

  ┌─────────────────────────────────────────────────────────────────┐
  │                    COMPARISON                                     │
  │                                                                  │
  │  Load Balancer    │ WS Support │ Notes                           │
  │  ─────────────────┼────────────┼──────────────────────────────── │
  │  AWS ALB          │  ✅ Yes    │ Native WS support, idle timeout │
  │                   │            │ up to 4000s                     │
  │  AWS NLB          │  ✅ Yes    │ Layer 4, passes TCP through.    │
  │                   │            │ No HTTP inspection, very fast.  │
  │  AWS API Gateway  │  ✅ Yes    │ WebSocket API type. Route-based │
  │                   │            │ ($connect, $disconnect, $default)│
  │  Nginx            │  ✅ Yes    │ Needs proxy_http_version 1.1 +  │
  │                   │            │ upgrade headers                 │
  │  HAProxy          │  ✅ Yes    │ Needs timeout tunnel setting    │
  │  Cloudflare       │  ✅ Yes    │ Automatic WS detection          │
  │  Traefik          │  ✅ Yes    │ Auto-detects Upgrade header     │
  │  Classic ELB      │  ❌ No     │ Does NOT support WebSocket      │
  └─────────────────────────────────────────────────────────────────┘
```

### AWS ALB Configuration for WebSockets

```
                    ALB + WebSocket Flow
═══════════════════════════════════════════════════════════════════════

Client              AWS ALB                    Target Group (WS Servers)
  │                    │                              │
  │  wss://api.example.com/ws                         │
  │──── TLS + HTTP Upgrade ───►│                      │
  │                            │                      │
  │                    ALB detects Upgrade: websocket  │
  │                    Routes to target group          │
  │                            │── HTTP Upgrade ──────►│
  │                            │◄── 101 ──────────────│
  │                            │                      │
  │  ALB becomes transparent   │                      │
  │  TCP pass-through for      │                      │
  │  WebSocket frames          │                      │
  │◄═══════ WS frames ════════►│◄═════ WS frames ═══►│
  │                            │                      │
  │  Idle timeout applies:     │                      │
  │  if no frame in X seconds, │                      │
  │  ALB closes connection     │                      │
```

ALB settings:
```
Target Group Attributes:
  - deregistration_delay.timeout_seconds: 300  (drain time during deploys)
  - stickiness.enabled: true (optional)
  - stickiness.type: lb_cookie

Listener:
  - Protocol: HTTPS (port 443)
  - Default action: forward to target group

ALB Attributes:
  - idle_timeout.timeout_seconds: 3600  (1 hour — CRITICAL for WebSockets)
```

### AWS API Gateway (WebSocket API)

AWS API Gateway has a dedicated **WebSocket API** type with route-based handling:

```
                    API Gateway WebSocket Routes
═══════════════════════════════════════════════════════════════════════

Client                    API Gateway                    Lambda / Backend
  │                            │                              │
  │  wss://xyz.execute-api     │                              │
  │──── Connect ──────────────►│                              │
  │                            │── $connect route ───────────►│
  │                            │   (auth, store connectionId) │
  │                            │◄── 200 ─────────────────────│
  │◄── Connection established ─│                              │
  │                            │                              │
  │══ {"action":"bid",...} ═══►│                              │
  │                            │── $default route ───────────►│
  │                            │   (or custom route based on  │
  │                            │    "action" field in message) │
  │                            │◄── response ────────────────│
  │◄══ {"status":"accepted"} ══│                              │
  │                            │                              │
  │  ✗ Client disconnects      │                              │
  │──── Close ────────────────►│                              │
  │                            │── $disconnect route ────────►│
  │                            │   (cleanup connectionId)     │
  │                            │                              │
```

API Gateway WebSocket specifics:
- **Connection ID**: API Gateway assigns each connection a unique ID. Store it in DynamoDB to push messages later.
- **Routes**: `$connect`, `$disconnect`, `$default`, plus custom routes (matched against JSON message body fields).
- **Push from backend**: Use the `@connections` API to push messages to specific connection IDs.
- **Limits**: 128KB max frame size, 2-hour idle timeout (with pings), 500 new connections/second default.

```javascript
// Push message to a connected client via API Gateway Management API
const { ApiGatewayManagementApi } = require('aws-sdk');

const apigw = new ApiGatewayManagementApi({
  endpoint: 'https://xyz.execute-api.region.amazonaws.com/prod'
});

await apigw.postToConnection({
  ConnectionId: 'abc123',
  Data: JSON.stringify({ event: 'new_bid', amount: 500 })
}).promise();
```

### Nginx Configuration for WebSockets

```nginx
upstream websocket_backend {
    # Optionally use ip_hash for sticky sessions
    ip_hash;

    server ws-server-1:8080;
    server ws-server-2:8080;
    server ws-server-3:8080;
}

server {
    listen 443 ssl;
    server_name api.example.com;

    ssl_certificate     /etc/ssl/cert.pem;
    ssl_certificate_key /etc/ssl/key.pem;

    location /ws {
        proxy_pass http://websocket_backend;

        # CRITICAL: These headers enable the WebSocket upgrade
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";

        # Pass client info
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;

        # Long timeouts for WebSocket
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;

        # Disable buffering
        proxy_buffering off;
    }
}
```

Without `proxy_http_version 1.1` and the Upgrade headers, Nginx will treat the WebSocket upgrade as a failed HTTP request and return 400.

---

## 4. How the WebSocket Server Holds Connections

### Connection Parking (Same Concept as SSE, Different Protocol)

```
┌─────────────────────────────────────────────────────┐
│              WS SERVER EVENT LOOP                     │
│                                                      │
│  Parked WebSocket Connections:                       │
│  ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐       │
│  │ ws_1   │ │ ws_2   │ │ ws_3   │ │ ws_4   │ ...   │
│  │ ~8KB   │ │ ~8KB   │ │ ~8KB   │ │ ~8KB   │       │
│  │ bidir  │ │ bidir  │ │ bidir  │ │ bidir  │       │
│  └────────┘ └────────┘ └────────┘ └────────┘       │
│                                                      │
│  Active Work:                                        │
│  - Redis subscriber callback (incoming events)       │
│  - Ping/Pong timer (fires every 30s)                 │
│  - onMessage handlers (client sends data)            │
│  - onClose handlers (cleanup)                        │
└─────────────────────────────────────────────────────┘
```

Key difference from SSE: The server listens for **incoming messages** from the client on the same connection. This makes it bidirectional.

### Server Implementation (Node.js + ws library)

```javascript
const WebSocket = require('ws');
const Redis = require('ioredis');
const http = require('http');

const server = http.createServer();
const wss = new WebSocket.Server({ server });
const subscriber = new Redis();
const publisher = new Redis();

// Connection maps
const topicMap = new Map();  // topic → Set<ws>
const userMap = new Map();   // userId → Set<ws>

wss.on('connection', (ws, req) => {
  // Authenticate from query string or first message
  const userId = authenticateFromRequest(req);
  if (!userId) return ws.close(4001, 'Unauthorized');

  ws.userId = userId;
  ws.isAlive = true;
  ws.topics = new Set();

  // Add to user map
  if (!userMap.has(userId)) userMap.set(userId, new Set());
  userMap.get(userId).add(ws);

  // Handle incoming messages from client
  ws.on('message', (raw) => {
    const msg = JSON.parse(raw);

    switch (msg.action) {
      case 'subscribe':
        subscribeTopic(ws, msg.topic);
        break;
      case 'unsubscribe':
        unsubscribeTopic(ws, msg.topic);
        break;
      case 'bid':
        handleBid(ws, msg);
        break;
      case 'ping':
        ws.send(JSON.stringify({ action: 'pong' }));
        break;
    }
  });

  // Pong response for server-initiated pings
  ws.on('pong', () => { ws.isAlive = true; });

  // Cleanup on close
  ws.on('close', () => cleanup(ws));
  ws.on('error', () => cleanup(ws));
});

function subscribeTopic(ws, topic) {
  if (!topicMap.has(topic)) {
    topicMap.set(topic, new Set());
    subscriber.subscribe(topic);
  }
  topicMap.get(topic).add(ws);
  ws.topics.add(topic);
}

function unsubscribeTopic(ws, topic) {
  topicMap.get(topic)?.delete(ws);
  ws.topics.delete(topic);
  if (topicMap.get(topic)?.size === 0) {
    topicMap.delete(topic);
    subscriber.unsubscribe(topic);
  }
}

function cleanup(ws) {
  // Remove from all topics
  for (const topic of ws.topics) {
    unsubscribeTopic(ws, topic);
  }
  // Remove from user map
  userMap.get(ws.userId)?.delete(ws);
  if (userMap.get(ws.userId)?.size === 0) userMap.delete(ws.userId);
}
```

---

## 5. Bidirectional Communication Patterns

### Client-to-Server Messages (What Makes WS Different From SSE)

With SSE, the client must use a separate HTTP POST to send data. With WebSockets, client sends over the same connection:

```
                    BIDIRECTIONAL AUCTION EXAMPLE
═══════════════════════════════════════════════════════════════════════

Client A (bidder)            WS Server                    Client B (watcher)
      │                          │                              │
      │  {"action":"subscribe",  │                              │
      │   "topic":"auction_123"} │  ← Client subscribes         │
      │═════════════════════════►│                              │
      │                          │                              │
      │                          │  {"action":"subscribe",      │
      │                          │   "topic":"auction_123"}     │
      │                          │◄═════════════════════════════│
      │                          │                              │
      │  {"action":"bid",        │                              │
      │   "amount": 500}         │  ← Client bids (same conn!) │
      │═════════════════════════►│                              │
      │                          │                              │
      │                          │── Process bid                │
      │                          │── Publish to Redis           │
      │                          │── Broadcast to topic:        │
      │                          │                              │
      │  {"event":"new_bid",     │  {"event":"new_bid",         │
      │   "amount":500,          │   "amount":500,              │
      │   "status":"your_bid"}   │   "user":"A"}               │
      │◄═════════════════════════│═════════════════════════════►│
      │                          │                              │
      │  {"action":"bid",        │                              │
      │   "amount": 550}         │                              │
      │═════════════════════════►│                              │
      │                          │── broadcast ──               │
      │◄═════════════════════════│═════════════════════════════►│
```

### Message Protocol Design

Since WebSocket is raw frames (no built-in routing like HTTP paths), you define your own message protocol:

```json
// Client → Server messages
{ "action": "subscribe", "topic": "auction_123" }
{ "action": "unsubscribe", "topic": "auction_123" }
{ "action": "bid", "auctionId": "123", "amount": 500 }
{ "action": "typing", "roomId": "chat_456" }

// Server → Client messages
{ "event": "new_bid", "auctionId": "123", "amount": 500, "user": "alice" }
{ "event": "subscribed", "topic": "auction_123", "success": true }
{ "event": "error", "code": "INSUFFICIENT_FUNDS", "message": "..." }
{ "event": "typing", "roomId": "chat_456", "user": "bob" }
```

---

## 6. Connection Routing & Multi-Instance Distribution

### Same Pattern as SSE, But Bidirectional

```
                    MULTI-INSTANCE ROUTING WITH REDIS
═══════════════════════════════════════════════════════════════════════

                         ┌─────────────────────┐
                         │    Redis Pub/Sub     │
                         └──────────┬───────────┘
                                    │
              ┌─────────────────────┼─────────────────────┐
              │                     │                     │
              ▼                     ▼                     ▼
     ┌──────────────┐     ┌──────────────┐     ┌──────────────┐
     │ WS Server 1  │     │ WS Server 2  │     │ WS Server 3  │
     │              │     │              │     │              │
     │ topicMap:    │     │ topicMap:    │     │ topicMap:    │
     │ auction_123: │     │ auction_123: │     │ auction_123: │
     │  [A, B]     │     │  [C, D, E]  │     │  [F]        │
     │              │     │              │     │              │
     │ Can RECEIVE  │     │ Can RECEIVE  │     │ Can RECEIVE  │
     │ messages from│     │ messages from│     │ messages from│
     │ A and B      │     │ C, D, and E  │     │ F            │
     └──────────────┘     └──────────────┘     └──────────────┘
```

When Client A sends a bid on WS Server 1:
1. WS Server 1 processes the bid
2. Publishes event to Redis: `PUBLISH auction_123 {"event":"new_bid",...}`
3. ALL instances (including Server 1) receive the Redis message
4. Each instance broadcasts to its local connections

```javascript
// Redis message handler — same on every instance
subscriber.on('message', (channel, raw) => {
  const data = JSON.parse(raw);
  const connections = topicMap.get(channel);
  if (!connections) return;

  const payload = JSON.stringify(data);
  for (const ws of connections) {
    if (ws.readyState === WebSocket.OPEN) {
      ws.send(payload);
    }
  }
});
```

---

## 7. Failure Concerns & Edge Cases

### A. Ghost Connections (Same Problem as SSE)

```
                    DETECTION VIA PING/PONG
═══════════════════════════════════════════════════════════════════════

WS Server                                        Client (maybe dead)
    │                                                 │
    │── WebSocket Ping frame (opcode 0x9) ──────────► │ (or ✗ if dead)
    │                                                 │
    │   Wait 30 seconds for Pong...                   │
    │                                                 │
    │   Case 1: Pong received ✅                       │
    │◄── WebSocket Pong frame (opcode 0xA) ───────────│
    │   → Connection alive, reset timer               │
    │                                                 │
    │   Case 2: No Pong within timeout ❌              │
    │   → Mark as dead, terminate, cleanup            │
    │                                                 │
```

**Implementation:**

```javascript
// Server-side ping interval (every 30 seconds)
const pingInterval = setInterval(() => {
  for (const ws of wss.clients) {
    if (!ws.isAlive) {
      // Missed last pong — connection is dead
      ws.terminate();
      cleanup(ws);
      return;
    }
    ws.isAlive = false;       // Reset flag
    ws.ping();                // Send ping, expect pong
  }
}, 30000);

// Pong handler resets the flag
wss.on('connection', (ws) => {
  ws.isAlive = true;
  ws.on('pong', () => { ws.isAlive = true; });
});
```

WebSocket has **protocol-level ping/pong** (opcodes 0x9/0xA), unlike SSE which uses application-level comment lines. Browsers handle pong responses automatically — no client-side code needed.

### B. Missed Messages During Disconnects

Unlike SSE, WebSocket does NOT have a built-in `Last-Event-ID` mechanism. You must implement replay yourself:

```
                    CUSTOM REPLAY MECHANISM
═══════════════════════════════════════════════════════════════════════

Client                           WS Server                     Redis
  │                                  │                            │
  │  Connection drops...             │                            │
  │  ✗ (offline for 10 seconds)      │                            │
  │                                  │                            │
  │  Reconnects:                     │                            │
  │  ws = new WebSocket(url)         │                            │
  │══════════════════════════════════►│                            │
  │                                  │                            │
  │  {"action":"subscribe",          │                            │
  │   "topic":"auction_123",         │                            │
  │   "lastEventId": 105}           │                            │
  │══════════════════════════════════►│                            │
  │                                  │── ZRANGEBYSCORE            │
  │                                  │   events:auction_123       │
  │                                  │   106 → +inf ────────────►│
  │                                  │◄── [106, 107, 108] ───────│
  │                                  │                            │
  │◄══ {id:106, event: ...} ═════════│                            │
  │◄══ {id:107, event: ...} ═════════│  (replay missed)           │
  │◄══ {id:108, event: ...} ═════════│                            │
  │                                  │                            │
  │◄══ {id:109, event: ...} ═════════│  (live from here)          │
```

Client-side reconnection with replay:

```javascript
class ReconnectingWebSocket {
  constructor(url, topics) {
    this.url = url;
    this.topics = topics;
    this.lastEventIds = {};  // topic → last seen event ID
    this.connect();
  }

  connect() {
    this.ws = new WebSocket(this.url);

    this.ws.onopen = () => {
      // Re-subscribe with last known event IDs
      for (const topic of this.topics) {
        this.ws.send(JSON.stringify({
          action: 'subscribe',
          topic,
          lastEventId: this.lastEventIds[topic] || 0
        }));
      }
    };

    this.ws.onmessage = (event) => {
      const data = JSON.parse(event.data);
      // Track last event ID per topic
      if (data.id && data.topic) {
        this.lastEventIds[data.topic] = data.id;
      }
      this.onEvent(data);
    };

    this.ws.onclose = () => {
      // Auto-reconnect with exponential backoff
      setTimeout(() => this.connect(), this.getBackoff());
    };
  }
}
```

### C. Load Balancer Timeouts & Connection Draining

```
                    GRACEFUL DEPLOY WITH CONNECTION DRAINING
═══════════════════════════════════════════════════════════════════════

  Step 1: New version deployed to Server 2 (replacement)
  ────────────────────────────────────────────────────

  Load Balancer                  Server 2 (old)          Server 2' (new)
       │                              │                        │
       │  Stop sending NEW            │                        │
       │  connections to old ─────────│                        │
       │                              │                        │
       │  Existing WS connections     │                        │
       │  on old server STAY OPEN     │                        │
       │  during drain period         │                        │
       │  (300 seconds)               │                        │
       │                              │                        │

  Step 2: Server 2 sends close frame to existing connections
  ──────────────────────────────────────────────────────────

  Server 2 (draining)              Client
       │                              │
       │── Close frame (1001) ───────►│  "Going Away"
       │◄── Close frame (ack) ────────│
       │                              │
       │  Connection closed cleanly   │── reconnect to LB ──►
       │                              │   (routed to new server)
       │                              │

  Step 3: After drain timeout, old server is terminated
```

---

## 8. Redis for Multi-Instance WebSockets

### Architecture (Identical to SSE Pattern)

```
Bid Service                Redis                     WS Servers              Clients
     │                       │                          │                      │
     │  Process bid          │                          │                      │
     │  Commit to DB         │                          │                      │
     │                       │                          │                      │
     │── PUBLISH             │                          │                      │
     │   auction_123         │                          │                      │
     │   {bid: 500} ────────►│                          │                      │
     │                       │── fan-out ──────────────►│                      │
     │                       │                          │                      │
     │── ZADD                │                          │──► ws_1.send(...)    │
     │   events:auction_123  │                          │──► ws_2.send(...)    │
     │   score=eventId       │                          │──► ws_3.send(...)    │
     │   {event data} ──────►│                          │                      │
     │                       │                          │                      │
```

Same dual-write pattern:
1. **PUBLISH** for real-time delivery to all connected instances
2. **ZADD** to sorted set for replay cache (missed message recovery)

### Scaling Concerns (Same as SSE, Plus Additional)

**Additional WebSocket concern — Bidirectional load:**

With SSE, clients only receive. With WebSocket, clients also send. If 10,000 clients each send 5 messages/second, that's 50,000 inbound messages/second per server to handle, validate, and potentially fan-out through Redis.

```
                    INBOUND MESSAGE PRESSURE
═══════════════════════════════════════════════════════════════════════

  SSE (unidirectional):
  Server CPU = f(outbound broadcasts only)
  Redis load = publishers only

  WebSocket (bidirectional):
  Server CPU = f(outbound broadcasts + inbound message processing)
  Redis load = publishers + potentially every client message that
               triggers a broadcast

  Example:
  10K connections × 5 msgs/sec = 50K inbound msgs/sec
  Each triggers validation + Redis PUBLISH
  Redis sees 50K PUBLISH/sec + 50K × N fan-out deliveries
```

**Mitigations:**
- **Rate limiting per connection**: Max 10 messages/second per client
- **Message batching**: Accumulate events for 50ms before broadcasting
- **Separate read/write Redis instances**: Publishers write to primary, subscribers read from replicas
- **Sharded topics**: Instead of one Redis channel per auction, shard into `auction_123_shard_0` through `auction_123_shard_3`

---

## 9. WebSocket vs SSE: When to Use Which

```
┌──────────────────────┬────────────────────────────┬────────────────────────────┐
│                      │         SSE                 │        WebSocket            │
├──────────────────────┼────────────────────────────┼────────────────────────────┤
│ Direction            │ Server → Client only        │ Bidirectional               │
│ Protocol             │ HTTP (text/event-stream)    │ WS protocol (after upgrade) │
│ Reconnection         │ Built-in (browser auto)     │ Manual (must implement)     │
│ Last-Event-ID        │ Built-in spec feature       │ Must implement yourself     │
│ Binary data          │ No (text only)              │ Yes (binary frames)         │
│ Multiplexing         │ Limited (one stream/conn)   │ Custom (multiplex in msgs)  │
│ Proxy/firewall compat│ Better (looks like HTTP)    │ Worse (different protocol)  │
│ Load balancer config │ Simpler (HTTP timeouts)     │ Needs upgrade support       │
│ Overhead per message │ ~50 bytes (data: ...\n\n)   │ 2-14 bytes (frame header)   │
│ Client sends data    │ Separate HTTP POST needed   │ Same connection             │
├──────────────────────┼────────────────────────────┼────────────────────────────┤
│ Best for             │ Notifications, feeds,       │ Chat, gaming, trading,      │
│                      │ dashboards, live scores     │ collaboration, anything     │
│                      │ (server pushes, client      │ requiring fast two-way      │
│                      │  mostly just listens)       │ communication               │
└──────────────────────┴────────────────────────────┴────────────────────────────┘
```

**Rule of thumb:** If the client mostly listens (dashboards, notifications, live feeds), use SSE — it's simpler, has built-in recovery, and works better with HTTP infrastructure. If the client actively sends data on the same connection (chat, gaming, collaborative editing), use WebSockets.

---

## 10. Complete Connection Lifecycle Summary

```
┌──────────────────────────────────────────────────────────────────────────┐
│                                                                           │
│  ┌──────────┐    ┌──────────────┐    ┌────────────┐    ┌─────────────┐  │
│  │ UPGRADE  │───►│  CONNECTED   │───►│  DRAINING  │───►│  CLEANED UP │  │
│  └──────────┘    └──────────────┘    └────────────┘    └─────────────┘  │
│                                                                           │
│  • TLS handshake  • Bidirectional     • Server sends    • Remove from    │
│  • HTTP Upgrade     messaging           close frame       topicMap       │
│  • Auth check     • Ping/Pong 30s    • Wait for ack    • Remove from    │
│  • Add to maps    • Redis Pub/Sub    • Timeout 5s        userMap        │
│  • Subscribe to   • Rate limiting    • Force close     • Unsubscribe    │
│    Redis channels • Replay on join     if needed         if last conn   │
│                                                         • Free memory    │
└──────────────────────────────────────────────────────────────────────────┘
```
