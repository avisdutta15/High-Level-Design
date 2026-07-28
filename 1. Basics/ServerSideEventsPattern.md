# Server-Side Events (SSE) Pattern

## Architecture Overview

```
┌──────────┐   GET /stream (held open)    ┌────────────┐    Subscribe     ┌───────────┐
│  Client  │ ◄════════════════════════════ │ SSE Server │ ◄──────────────  │   Redis   │
│ (Browser)│   text/event-stream           │            │    Pub/Sub       │  Pub/Sub  │
└──────────┘                               └────────────┘                  └───────────┘
                                                                                 ▲
┌──────────┐   POST /bid (standard HTTP)   ┌─────────────┐   Publish            │
│  Client  │ ─────────────────────────────►│ Bid Service  │ ────────────────────┘
│ (Browser)│   closes after response       │             │
└──────────┘                               └─────────────┘
```

Two completely separate connections:
- **Read path (SSE):** Long-lived, held open for minutes/hours, server pushes data to client
- **Write path (POST):** Standard request-response, closes immediately after response

---

## 1. How Does the SSE Server Hold the HTTP Connection?

Unlike a standard HTTP request that closes immediately after the response is sent, SSE intentionally keeps the TCP socket open. Here is exactly how it works at the network and application layer.

### The SSE Handshake & Connection Lifecycle

```
Client (Browser)                                         SSE Server
      |                                                       |
      |  ① HTTP GET /auction/123/stream                       |
      |     Accept: text/event-stream                         |
      |──────────────────────────────────────────────────────►|
      |                                                       |
      |  ② HTTP 200 OK                                        |
      |     Content-Type: text/event-stream                   |
      |     Connection: keep-alive                            |
      |     Transfer-Encoding: chunked                        |
      |     Cache-Control: no-cache                           |
      |◄──────────────────────────────────────────────────────|
      |                                                       |
      |        ┌─── TCP SOCKET REMAINS OPEN ───┐              |
      |        │                               │              |
      |        │  Connection is "parked"       │              |
      |        │  in the server's event loop   │              |
      |        │  consuming ~few KB of RAM     │              |
      |        │  and zero CPU while idle      │              |
      |        │                               │              |
      |        └───────────────────────────────┘              |
      |                                                       |
      |  ③ Server pushes data when available:                 |
      |     data: {"bid": 500}\n\n                            |
      |◄──────────────────────────────────────────────────────|
      |                                                       |
      |  ④ ... more data pushed anytime ...                   |
      |     data: {"bid": 550}\n\n                            |
      |◄──────────────────────────────────────────────────────|
      |                                                       |
      |  ⑤ Heartbeat (every 15s to keep connection alive):    |
      |     :ping\n\n                                         |
      |◄──────────────────────────────────────────────────────|
      |                                                       |
```

### Step-by-Step Breakdown

**Step 1 — The Handshake (Client Initiates)**

The browser opens an `EventSource` and sends a standard HTTP GET with a specific header:

```javascript
const source = new EventSource('/auction/123/stream');
```

This translates to:
```
GET /auction/123/stream HTTP/1.1
Host: api.example.com
Accept: text/event-stream
Cache-Control: no-cache
Connection: keep-alive
```

**Step 2 — The Server Response (Connection Held Open)**

The server responds with `200 OK` but includes critical headers that signal "this response is not finished":

```
HTTP/1.1 200 OK
Content-Type: text/event-stream
Connection: keep-alive
Transfer-Encoding: chunked
Cache-Control: no-cache
X-Accel-Buffering: no
```

Key points:
- **No `Content-Length` header** — This tells the HTTP layer "I don't know how much data I'll send; keep the connection open."
- **`Transfer-Encoding: chunked`** — Data arrives in chunks over time, not all at once.
- **`Cache-Control: no-cache`** — Prevents proxies from buffering the stream.
- **`X-Accel-Buffering: no`** — Tells Nginx to disable response buffering for this endpoint.

**Step 3 — The Event Loop (Parking the Socket)**

The server does NOT block a thread waiting for data. In asynchronous frameworks, the open TCP socket is "parked" in the event loop:

```
┌─────────────────────────────────────────────────────┐
│              SERVER EVENT LOOP                        │
│                                                      │
│  Parked Connections (idle, near-zero CPU):           │
│  ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐       │
│  │ conn_1 │ │ conn_2 │ │ conn_3 │ │ conn_4 │ ...   │
│  │ ~4KB   │ │ ~4KB   │ │ ~4KB   │ │ ~4KB   │       │
│  └────────┘ └────────┘ └────────┘ └────────┘       │
│                                                      │
│  Active Work:                                        │
│  - Redis subscriber callback                         │
│  - Heartbeat timer (fires every 15s)                 │
│  - New connection handler                            │
└─────────────────────────────────────────────────────┘
```

Resource cost per idle connection: **~2-8 KB of RAM** for the socket buffer. A server with 4GB of RAM can hold ~100,000+ idle SSE connections.

**Step 4 — Pushing Data**

When the SSE server receives a message from Redis Pub/Sub, it iterates through its open sockets and writes a text payload:

```
data: {"bid": 500, "user": "alice", "timestamp": 1705485600}\n\n
```

The **double newline** (`\n\n`) tells the browser the event is complete and triggers the `onmessage` callback.

**Step 5 — Heartbeat Pings**

To prevent firewalls, load balancers, and proxies from killing the connection due to inactivity:

```
:ping\n\n
```

The colon prefix makes this a **comment** in the SSE spec — the browser ignores it, but it keeps the TCP connection active.

### Server Implementation (Node.js Example)

```javascript
const express = require('express');
const Redis = require('ioredis');
const app = express();
const subscriber = new Redis();

// Map of active connections per auction
const topicMap = new Map(); // auctionId → Set<response>

app.get('/auction/:id/stream', (req, res) => {
  const auctionId = req.params.id;

  // Set SSE headers — this keeps the connection open
  res.writeHead(200, {
    'Content-Type': 'text/event-stream',
    'Connection': 'keep-alive',
    'Cache-Control': 'no-cache',
    'X-Accel-Buffering': 'no'
  });

  // Park this connection in our topic map
  if (!topicMap.has(auctionId)) {
    topicMap.set(auctionId, new Set());
  }
  topicMap.get(auctionId).add(res);

  // Heartbeat every 15 seconds
  const heartbeat = setInterval(() => {
    res.write(':ping\n\n');
  }, 15000);

  // Cleanup on disconnect
  req.on('close', () => {
    clearInterval(heartbeat);
    topicMap.get(auctionId)?.delete(res);
  });
});

// When Redis publishes a message, push to all subscribers
subscriber.subscribe('auction_updates');
subscriber.on('message', (channel, message) => {
  const { auctionId, payload } = JSON.parse(message);
  const connections = topicMap.get(auctionId);
  if (connections) {
    for (const res of connections) {
      res.write(`data: ${JSON.stringify(payload)}\n\n`);
    }
  }
});
```

---

## 2. What If Processing the Bid Takes Time?

### The Critical Distinction: Read vs Write Are Separate Connections

```
┌──────────────────────────────────────────────────────────────────────────┐
│                        USER A's BROWSER                                    │
│                                                                           │
│  Connection 1 (SSE - READ):          Connection 2 (POST - WRITE):         │
│  ┌─────────────────────────────┐     ┌─────────────────────────────┐      │
│  │ GET /auction/123/stream     │     │ POST /bid                    │      │
│  │ Status: OPEN for 45 min     │     │ Status: Waiting for response │      │
│  │ Last event: 3 seconds ago   │     │ Sent: 5 seconds ago          │      │
│  └─────────────────────────────┘     └─────────────────────────────┘      │
└──────────────────────────────────────────────────────────────────────────┘
```

These are **two completely independent TCP connections**. One being slow does not affect the other.

### Timeline: Slow Bid Processing

```
Time    Client                  API Gateway       Bid Service         Redis        SSE Server      Client (SSE)
─────   ──────                  ───────────       ───────────         ─────        ──────────      ────────────
T+0s    POST /bid ─────────────►│─────────────────►│                    │              │               │
        {amount: 500}           │                  │ Validate bid       │              │               │
                                │                  │ Acquire DB lock    │              │               │
T+1s                            │                  │ Check balance...   │              │               │
T+2s                            │                  │ Update auction...  │              │               │
T+3s                            │                  │ Commit txn...      │              │               │
                                │                  │                    │              │               │
        Meanwhile, SSE stream   │                  │                    │              │  :ping\n\n    │
        is completely           │                  │                    │              │──────────────►│
        undisturbed             │                  │                    │              │  (heartbeat)  │
                                │                  │                    │              │               │
T+5s                            │                  │ ✅ Committed!       │              │               │
                                │                  │─── PUBLISH ────────►│              │               │
        ◄── 200 OK ────────────│◄─────────────────│                    │              │               │
        (or 202 Accepted)       │                  │                    │              │               │
                                │                  │                    │──subscribe──►│               │
T+5.1s                          │                  │                    │              │  data: {...}  │
                                │                  │                    │              │──────────────►│
                                │                  │                    │              │               │ onmessage!
                                │                  │                    │              │               │ UI updates
```

### Two Response Strategies for the POST

**Option A: Synchronous (wait for commit)**
```
POST /bid → 200 OK after 5 seconds
  Pro: Client gets confirmation the bid was accepted
  Con: Feels slow, might timeout
```

**Option B: Async with 202 Accepted (recommended for slow processing)**
```
POST /bid → 202 Accepted immediately
  Body: { "status": "processing", "bidId": "bid_789" }

  Pro: Instant response, no timeout risk
  Con: Client must rely on SSE stream for final confirmation
```

With Option B, the client knows its bid is being processed, and the actual result arrives via the SSE stream moments later.

### Heartbeat Keeps SSE Alive During Quiet Periods

```
SSE Connection Timeline:
═══════════════════════════════════════════════════════════════════
  T+0s         T+15s        T+30s        T+35s       T+45s
   │             │             │             │            │
   │  :ping\n\n  │  :ping\n\n  │             │ :ping\n\n  │
   │◄────────────│◄────────────│             │◄───────────│
   │             │             │  data: ...  │            │
   │             │             │◄────────────│            │
   │             │             │  (real msg) │            │
═══════════════════════════════════════════════════════════════════
                                    ▲
                    Load balancer sees traffic,
                    resets its 60s idle timeout
```

Without heartbeats, a load balancer configured with a 60-second idle timeout would kill the SSE connection during quiet periods.

---

## 3. Failure Concerns & Edge Cases

### A. The "Ghost Connection" Memory Leak

**The Problem:**

When a user loses connectivity (drives into a tunnel, laptop sleeps, 5G drops), the TCP connection dies silently on the client side. But the server doesn't know — TCP doesn't instantly notify the remote side of a dropped connection. The server still holds a reference to the dead socket in memory.

```
                    BEFORE (5,000 real users)
┌────────────────────────────────────────────────────┐
│  SSE Server Memory                                  │
│                                                     │
│  TopicMap['auction_123']:                           │
│    ✅ conn_1 (active)                               │
│    ✅ conn_2 (active)                               │
│    ✅ conn_3 (active)                               │
│    ...                                              │
│    ✅ conn_5000 (active)                            │
│                                                     │
│  RAM Usage: ~40 MB (5000 × 8KB)                    │
└────────────────────────────────────────────────────┘


                    AFTER (ghost accumulation over hours)
┌────────────────────────────────────────────────────┐
│  SSE Server Memory                                  │
│                                                     │
│  TopicMap['auction_123']:                           │
│    ✅ conn_1 (active)                               │
│    💀 conn_2 (DEAD - user lost wifi 2 hours ago)    │
│    ✅ conn_3 (active)                               │
│    💀 conn_4 (DEAD - phone went to sleep)           │
│    💀 conn_5 (DEAD - user closed laptop)            │
│    ...                                              │
│    💀 conn_48000 (DEAD - accumulated over 8 hours)  │
│                                                     │
│  RAM Usage: ~400 MB (and growing!)                  │
│  ⚠️  Server approaching OOM crash                   │
└────────────────────────────────────────────────────┘
```

**The Solution: Heartbeat Write Failure Detection**

```
SSE Server                                          Dead Client
    |                                                    ✗ (disconnected)
    |                                                    |
    |-- write(":ping\n\n") ───────────────────────────► ✗
    |                                                    |
    |   TCP layer detects write failure                  |
    |   (EPIPE / ECONNRESET / timeout)                  |
    |                                                    |
    |   ┌──────────────────────────────┐                |
    |   │ Error handler fires:          │                |
    |   │  1. Remove from TopicMap      │                |
    |   │  2. Remove from UserMap       │                |
    |   │  3. Destroy socket            │                |
    |   │  4. Free memory               │                |
    |   └──────────────────────────────┘                |
```

**Implementation:**

```javascript
// Heartbeat with ghost detection
const heartbeat = setInterval(() => {
  try {
    const success = res.write(':ping\n\n');
    if (!success) {
      // Back-pressure: client can't keep up, likely dead
      cleanupConnection(res, auctionId, userId);
    }
  } catch (err) {
    // Write failed: connection is definitely dead
    cleanupConnection(res, auctionId, userId);
  }
}, 15000);

// Also listen for TCP close event
req.on('close', () => cleanupConnection(res, auctionId, userId));
req.on('error', () => cleanupConnection(res, auctionId, userId));

function cleanupConnection(res, auctionId, userId) {
  clearInterval(heartbeat);
  topicMap.get(auctionId)?.delete(res);
  userMap.get(userId)?.delete(res);
  res.end();
}
```

Additional safeguards:
- **TCP Keep-Alive** at the OS level (sends TCP probes every 60s)
- **Application-level timeout**: If a connection hasn't acknowledged a heartbeat in 60s, force-close it
- **Periodic audit**: Every 5 minutes, iterate all connections and verify they're still writable

---

### B. Missed Messages During Disconnects

**The Problem:**

If the client's internet drops for 10 seconds, they miss any events broadcasted during that window. When they reconnect, they have a stale view.

```
Timeline:
═══════════════════════════════════════════════════════════════

  T+0s        T+2s         T+5s         T+8s        T+12s
   │            │             │             │            │
   │ event 101  │  event 102  │  event 103  │ event 104  │
   │◄───────────│             │             │◄───────────│
   │            │             │             │            │
   │     ██████████████████████████████     │            │
   │     █  DISCONNECTED (3s-10s)     █     │            │
   │     █  Missed: 102, 103          █     │            │
   │     ██████████████████████████████     │            │
   │                                        │            │
═══════════════════════════════════════════════════════════════
                    ▲
        Client has NO IDEA events 102 and 103 happened
```

**The Solution: Event IDs + Last-Event-ID Recovery**

SSE has a built-in recovery mechanism in the spec:

```
            RECOVERY FLOW
═══════════════════════════════════════════════════════════════════

  ① Server sends events WITH IDs:
  ─────────────────────────────────
      id: 101\n
      data: {"bid": 400}\n\n

      id: 102\n
      data: {"bid": 450}\n\n        ← Client disconnects here

      id: 103\n
      data: {"bid": 475}\n\n        ← Missed

  ② Client reconnects automatically (browser does this!):
  ───────────────────────────────────────────────────────
      GET /auction/123/stream
      Last-Event-ID: 101              ← "I last saw event 101"

  ③ Server catches client up:
  ────────────────────────────
      id: 102\n
      data: {"bid": 450}\n\n          ← Replayed from cache

      id: 103\n
      data: {"bid": 475}\n\n          ← Replayed from cache

      id: 104\n
      data: {"bid": 500}\n\n          ← Live from here on
```

**Server-Side Implementation:**

```
┌──────────┐   Last-Event-ID: 101   ┌────────────┐   LRANGE events    ┌───────────┐
│  Client  │ ──────────────────────► │ SSE Server │ ─────────────────► │   Redis   │
│(reconnect)│                         │            │                    │  (List)   │
│          │ ◄─────────────────────  │            │ ◄───────────────── │           │
│          │   replay events 102-104 │            │   events 102-104   │           │
└──────────┘                         └────────────┘                    └───────────┘
```

```javascript
app.get('/auction/:id/stream', async (req, res) => {
  const auctionId = req.params.id;
  const lastEventId = req.headers['last-event-id'];

  // Set SSE headers
  res.writeHead(200, { /* ... SSE headers ... */ });

  // If reconnecting, replay missed events from Redis cache
  if (lastEventId) {
    const missedEvents = await getMissedEvents(auctionId, parseInt(lastEventId));
    for (const event of missedEvents) {
      res.write(`id: ${event.id}\ndata: ${JSON.stringify(event.data)}\n\n`);
    }
  }

  // Continue with live events...
  parkConnection(res, auctionId);
});

async function getMissedEvents(auctionId, afterId) {
  // Redis stores last N events per auction in a sorted set
  const events = await redis.zrangebyscore(
    `events:${auctionId}`,
    afterId + 1,  // from the one after last seen
    '+inf'        // to the latest
  );
  return events.map(JSON.parse);
}

// When publishing events, also cache them
async function publishEvent(auctionId, eventData) {
  const eventId = await redis.incr(`event_counter:${auctionId}`);

  // Store in sorted set for replay (keep last 1000 events, TTL 5 min)
  await redis.zadd(`events:${auctionId}`, eventId, JSON.stringify({
    id: eventId,
    data: eventData
  }));
  await redis.zremrangebyrank(`events:${auctionId}`, 0, -1001); // trim
  await redis.expire(`events:${auctionId}`, 300); // 5 min TTL

  // Publish for live subscribers
  await redis.publish(`auction_${auctionId}`, JSON.stringify({
    auctionId, payload: { id: eventId, ...eventData }
  }));
}
```

---

### C. Load Balancer Timeouts

**The Problem:**

Load balancers (AWS ALB, Nginx, HAProxy) are configured to kill HTTP connections that appear "idle" — typically after 60 seconds of no data transfer. SSE connections are idle by nature between events.

```
Client          Load Balancer (60s timeout)         SSE Server
  │                      │                              │
  │═══ SSE Connected ════│══════════════════════════════│
  │                      │                              │
  │  ... 60 seconds of silence ...                      │
  │                      │                              │
  │              ❌ LB KILLS CONNECTION                  │
  │◄── TCP RST ─────────│                              │
  │                      │     (server doesn't know)    │
  │                      │                              │
```

**The Solution: Heartbeats + LB Configuration**

```
Client          Load Balancer (timeout=3600s)        SSE Server
  │                      │                              │
  │═══ SSE Connected ════│══════════════════════════════│
  │                      │                              │
  │  ... 14 seconds ...  │                              │
  │                      │                              │
  │◄─── :ping\n\n ──────│◄──────── :ping\n\n ─────────│  (T+15s)
  │                      │                              │
  │     LB sees traffic, │                              │
  │     resets timer ✅   │                              │
  │                      │                              │
  │  ... 14 seconds ...  │                              │
  │                      │                              │
  │◄─── :ping\n\n ──────│◄──────── :ping\n\n ─────────│  (T+30s)
  │                      │                              │
  │     Timer reset ✅    │                              │
  │                      │                              │
```

**Configuration Examples:**

AWS ALB:
```
# Set idle timeout to 1 hour for the target group
aws elbv2 modify-target-group-attributes \
  --target-group-arn <arn> \
  --attributes Key=deregistration_delay.timeout_seconds,Value=3600
```

Nginx:
```nginx
location /stream {
    proxy_pass http://sse_backend;
    proxy_http_version 1.1;
    proxy_set_header Connection '';
    proxy_buffering off;
    proxy_cache off;

    # Critical: increase timeouts for SSE
    proxy_read_timeout 3600s;
    proxy_send_timeout 3600s;
    send_timeout 3600s;
}
```

HAProxy:
```
defaults
    timeout client  3600s
    timeout server  3600s
    timeout tunnel  3600s
```

---

## 4. Connection Routing

### How the Server Maps Connections to Topics and Users

When a client opens an SSE connection, the server needs to know:
1. **What topic** to send them (e.g., which auction)
2. **Who they are** (for user-specific notifications)

### The Routing Data Structure

```
┌───────────────────────────────────────────────────────────────────┐
│                     SSE SERVER MEMORY                               │
│                                                                    │
│  TopicMap (broadcast to many):                                     │
│  ┌─────────────────────────────────────────────────────────────┐  │
│  │ 'auction_123' → [ conn_1, conn_2, conn_3, conn_4 ]          │  │
│  │ 'auction_456' → [ conn_5, conn_6 ]                          │  │
│  │ 'auction_789' → [ conn_7, conn_8, conn_9 ]                  │  │
│  └─────────────────────────────────────────────────────────────┘  │
│                                                                    │
│  UserMap (direct notifications):                                   │
│  ┌─────────────────────────────────────────────────────────────┐  │
│  │ 'user_88' → [ conn_1, conn_5 ]    (watching 2 auctions)     │  │
│  │ 'user_42' → [ conn_2 ]            (watching 1 auction)      │  │
│  │ 'user_77' → [ conn_3, conn_7 ]    (watching 2 auctions)     │  │
│  └─────────────────────────────────────────────────────────────┘  │
│                                                                    │
└───────────────────────────────────────────────────────────────────┘
```

### Full Lifecycle: Connect → Route → Cleanup

```
        ① CONNECT
═══════════════════════════════════════════════════════════════════

Client (user_88)              SSE Server                        Redis
      │                           │                               │
      │  GET /stream?auctionId=123│                               │
      │  Authorization: Bearer... │                               │
      │──────────────────────────►│                               │
      │                           │                               │
      │                           │  Authenticate JWT → user_88   │
      │                           │  Park connection (res object)  │
      │                           │                               │
      │                           │  TopicMap['auction_123']       │
      │                           │    .add(conn)                  │
      │                           │                               │
      │                           │  UserMap['user_88']            │
      │                           │    .add(conn)                  │
      │                           │                               │
      │                           │── SUBSCRIBE auction_123 ──────►│
      │                           │                               │
      │◄── 200 OK (stream open) ──│                               │
      │                           │                               │


        ② ROUTE (message arrives)
═══════════════════════════════════════════════════════════════════

Bid Service                   Redis                    SSE Server
      │                         │                          │
      │── PUBLISH               │                          │
      │   auction_123:          │                          │
      │   {bid: 500} ──────────►│                          │
      │                         │── on('message') ────────►│
      │                         │                          │
      │                         │      ┌──────────────────────────────────┐
      │                         │      │ Route logic:                      │
      │                         │      │                                   │
      │                         │      │ topic = 'auction_123'             │
      │                         │      │ connections = TopicMap[topic]      │
      │                         │      │                                   │
      │                         │      │ for (conn of connections) {       │
      │                         │      │   conn.write('data: {...}\n\n')   │
      │                         │      │ }                                 │
      │                         │      └──────────────────────────────────┘
      │                         │                          │
      │                         │                          │──► conn_1: data: {"bid":500}\n\n
      │                         │                          │──► conn_2: data: {"bid":500}\n\n
      │                         │                          │──► conn_3: data: {"bid":500}\n\n
      │                         │                          │──► conn_4: data: {"bid":500}\n\n


        ③ CLEANUP (user disconnects)
═══════════════════════════════════════════════════════════════════

Client                        SSE Server
      │                           │
      │  ✗ (closes browser tab)   │
      │  TCP FIN ────────────────►│
      │                           │
      │                           │  'close' event fires on req
      │                           │
      │                           │  ┌─────────────────────────────┐
      │                           │  │ Cleanup:                     │
      │                           │  │                              │
      │                           │  │ 1. clearInterval(heartbeat)  │
      │                           │  │                              │
      │                           │  │ 2. TopicMap['auction_123']   │
      │                           │  │      .delete(conn)           │
      │                           │  │                              │
      │                           │  │ 3. UserMap['user_88']        │
      │                           │  │      .delete(conn)           │
      │                           │  │                              │
      │                           │  │ 4. res.end()                 │
      │                           │  │    (free socket resources)   │
      │                           │  │                              │
      │                           │  │ 5. If TopicMap['auction_123']│
      │                           │  │    is empty → UNSUBSCRIBE    │
      │                           │  │    from Redis channel        │
      │                           │  └─────────────────────────────┘
```

### Why Cleanup Is Crucial

Without proper cleanup, here's what happens over time:

```
Hour 0:  TopicMap['auction_123'] = 500 connections    (500 real users)
Hour 1:  TopicMap['auction_123'] = 1,200 connections  (500 real + 700 ghosts)
Hour 4:  TopicMap['auction_123'] = 4,000 connections  (400 real + 3,600 ghosts)
Hour 8:  ⚠️ OOM KILL — server dies
```

Every broadcast iterates ALL connections (including dead ones), wasting CPU on failed writes. Combined with the memory leak, this compounds into a crash.

### Complete Routing Implementation

```javascript
class SSEConnectionManager {
  constructor(redis) {
    this.topicMap = new Map();  // topic → Set<{res, userId, heartbeat}>
    this.userMap = new Map();   // userId → Set<{res, topic, heartbeat}>
    this.redis = redis;
  }

  connect(req, res, topic, userId) {
    // SSE headers
    res.writeHead(200, {
      'Content-Type': 'text/event-stream',
      'Connection': 'keep-alive',
      'Cache-Control': 'no-cache'
    });

    const conn = { res, userId, topic };

    // Start heartbeat
    conn.heartbeat = setInterval(() => {
      try {
        res.write(':ping\n\n');
      } catch (e) {
        this.disconnect(conn);
      }
    }, 15000);

    // Add to TopicMap
    if (!this.topicMap.has(topic)) this.topicMap.set(topic, new Set());
    this.topicMap.get(topic).add(conn);

    // Add to UserMap
    if (!this.userMap.has(userId)) this.userMap.set(userId, new Set());
    this.userMap.get(userId).add(conn);

    // Cleanup on close
    req.on('close', () => this.disconnect(conn));
    req.on('error', () => this.disconnect(conn));
  }

  broadcast(topic, eventId, data) {
    const connections = this.topicMap.get(topic);
    if (!connections) return;

    const payload = `id: ${eventId}\ndata: ${JSON.stringify(data)}\n\n`;

    for (const conn of connections) {
      try {
        conn.res.write(payload);
      } catch (e) {
        this.disconnect(conn); // Dead connection, clean it up
      }
    }
  }

  notifyUser(userId, eventId, data) {
    const connections = this.userMap.get(userId);
    if (!connections) return;

    const payload = `id: ${eventId}\ndata: ${JSON.stringify(data)}\n\n`;
    for (const conn of connections) {
      try {
        conn.res.write(payload);
      } catch (e) {
        this.disconnect(conn);
      }
    }
  }

  disconnect(conn) {
    clearInterval(conn.heartbeat);

    // Remove from TopicMap
    this.topicMap.get(conn.topic)?.delete(conn);
    if (this.topicMap.get(conn.topic)?.size === 0) {
      this.topicMap.delete(conn.topic);
      this.redis.unsubscribe(conn.topic); // No more listeners
    }

    // Remove from UserMap
    this.userMap.get(conn.userId)?.delete(conn);
    if (this.userMap.get(conn.userId)?.size === 0) {
      this.userMap.delete(conn.userId);
    }

    try { conn.res.end(); } catch (e) { /* already closed */ }
  }

  getStats() {
    return {
      totalTopics: this.topicMap.size,
      totalConnections: [...this.topicMap.values()].reduce((sum, s) => sum + s.size, 0),
      totalUsers: this.userMap.size
    };
  }
}
```

---

## Summary: SSE Connection Lifecycle

```
┌─────────────────────────────────────────────────────────────────────┐
│                                                                      │
│   ┌──────────┐     ┌──────────────┐     ┌────────────┐             │
│   │ CONNECT  │────►│  STREAMING   │────►│  CLEANUP   │             │
│   └──────────┘     └──────────────┘     └────────────┘             │
│                                                                      │
│   • Auth user       • Push events        • Remove from maps         │
│   • Park socket     • Heartbeat 15s      • Clear heartbeat          │
│   • Add to maps     • Detect dead conns  • Free socket memory       │
│   • Subscribe       • Replay on reconnect• Unsubscribe if empty     │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 5. Redis: The Backbone of Multi-Instance SSE

### Why Redis Pub/Sub Is Needed

In production, you don't run a single SSE server. You run multiple instances behind a load balancer for availability and scale. This creates a fundamental problem: **each instance only knows about its own connections.**

```
                    THE PROBLEM: Isolated Connection Maps
═══════════════════════════════════════════════════════════════════════

                        ┌──────────────────┐
                        │   Load Balancer   │
                        └────────┬─────────┘
                   ┌─────────────┼─────────────┐
                   ▼             ▼             ▼
          ┌──────────────┐ ┌──────────────┐ ┌──────────────┐
          │ SSE Server 1 │ │ SSE Server 2 │ │ SSE Server 3 │
          │              │ │              │ │              │
          │ TopicMap:    │ │ TopicMap:    │ │ TopicMap:    │
          │ auction_123: │ │ auction_123: │ │ auction_123: │
          │  [A, B, C]  │ │  [D, E]     │ │  [F, G, H]  │
          └──────────────┘ └──────────────┘ └──────────────┘

  User A,B,C connected     User D,E connected   User F,G,H connected
  to instance 1            to instance 2        to instance 3
```

When the Bid Service processes a new bid for `auction_123`, which server does it notify? It can't call all three directly — it doesn't know how many instances exist or where users are connected. Even if it did, that would create tight coupling.

**Without Redis:** Only users on the instance that receives the event see the update. Users on other instances see nothing.

```
          Bid Service publishes "new bid: $500" for auction_123

          ❌ WITHOUT REDIS:
          ─────────────────
          Server 1: Users A, B, C get the update ✅
          Server 2: Users D, E see nothing ❌
          Server 3: Users F, G, H see nothing ❌
```

**With Redis Pub/Sub:** Redis acts as a broadcast bus. Every SSE instance subscribes to relevant channels. When any service publishes an event, ALL instances receive it and push to their local connections.

```
          ✅ WITH REDIS PUB/SUB:
          ──────────────────────
          Bid Service → PUBLISH to Redis → ALL instances receive it
          Server 1: Users A, B, C get the update ✅
          Server 2: Users D, E get the update ✅
          Server 3: Users F, G, H get the update ✅
```

---

### How Pub/Sub Distributes Events Across Instances

```
                    FULL EVENT FLOW WITH REDIS
═══════════════════════════════════════════════════════════════════════

Bid Service              Redis Pub/Sub              SSE Servers              Clients
     │                        │                         │                      │
     │  PUBLISH               │                         │                      │
     │  channel: auction_123  │                         │                      │
     │  data: {bid: 500}      │                         │                      │
     │───────────────────────►│                         │                      │
     │                        │                         │                      │
     │                        │  Fan-out to ALL         │                      │
     │                        │  subscribers:           │                      │
     │                        │                         │                      │
     │                        │──► Server 1 ───────────►│──► User A: data:...\n\n
     │                        │    (subscribed)         │──► User B: data:...\n\n
     │                        │                         │──► User C: data:...\n\n
     │                        │                         │                      │
     │                        │──► Server 2 ───────────►│──► User D: data:...\n\n
     │                        │    (subscribed)         │──► User E: data:...\n\n
     │                        │                         │                      │
     │                        │──► Server 3 ───────────►│──► User F: data:...\n\n
     │                        │    (subscribed)         │──► User G: data:...\n\n
     │                        │                         │──► User H: data:...\n\n
     │                        │                         │                      │
```

**Each SSE server instance runs a Redis subscriber:**

```javascript
// Every SSE server instance does this on startup
const subscriber = new Redis(REDIS_URL);

// Subscribe to channels as users connect
function onUserConnect(auctionId) {
  if (!topicMap.has(auctionId)) {
    // First user on THIS instance watching this auction
    subscriber.subscribe(`auction_${auctionId}`);
    topicMap.set(auctionId, new Set());
  }
  // ...add connection to topicMap
}

// Handle incoming messages from Redis
subscriber.on('message', (channel, message) => {
  // channel = "auction_123"
  const auctionId = channel.replace('auction_', '');
  const { eventId, data } = JSON.parse(message);

  // Broadcast to LOCAL connections only
  const connections = topicMap.get(auctionId);
  if (connections) {
    const payload = `id: ${eventId}\ndata: ${JSON.stringify(data)}\n\n`;
    for (const conn of connections) {
      conn.res.write(payload);
    }
  }
});

// Unsubscribe when last user on this instance disconnects
function onLastUserDisconnect(auctionId) {
  subscriber.unsubscribe(`auction_${auctionId}`);
  topicMap.delete(auctionId);
}
```

**The Bid Service (publisher) doesn't know or care about SSE servers:**

```javascript
// Bid Service — after committing the bid to the database
async function onBidCommitted(auctionId, bid) {
  const eventId = await redis.incr(`event_counter:${auctionId}`);

  // Just publish. Redis handles fan-out to all SSE instances.
  await redis.publish(`auction_${auctionId}`, JSON.stringify({
    eventId,
    data: {
      bid: bid.amount,
      user: bid.userId,
      timestamp: Date.now()
    }
  }));
}
```

---

### Redis as the Event Replay Cache

Pub/Sub is fire-and-forget — if no subscriber is listening at the moment of publish, the message is lost. This is fine for live connections, but breaks the `Last-Event-ID` recovery flow. We need a separate data structure to cache recent events.

**Sorted Sets for ordered event replay:**

```
                    REDIS DATA STRUCTURES
═══════════════════════════════════════════════════════════════════════

  Pub/Sub Channel (live delivery):          Sorted Set (replay cache):
  ┌────────────────────────────┐           ┌────────────────────────────────┐
  │ Channel: auction_123       │           │ Key: events:auction_123        │
  │                            │           │                                │
  │ Subscribers:               │           │ Score │ Member                 │
  │   - SSE Server 1           │           │ ──────┼─────────────────────── │
  │   - SSE Server 2           │           │  101  │ {"id":101,"bid":400}   │
  │   - SSE Server 3           │           │  102  │ {"id":102,"bid":450}   │
  │                            │           │  103  │ {"id":103,"bid":475}   │
  │ Fire-and-forget.           │           │  104  │ {"id":104,"bid":500}   │
  │ No persistence.            │           │  105  │ {"id":105,"bid":550}   │
  │                            │           │                                │
  └────────────────────────────┘           │ TTL: 300 seconds (5 min)       │
                                           │ Max size: 1000 events          │
                                           └────────────────────────────────┘
```

**Dual-write on publish (live + cache):**

```javascript
async function publishEvent(auctionId, data) {
  const eventId = await redis.incr(`event_counter:${auctionId}`);
  const event = JSON.stringify({ id: eventId, data });

  // 1. Cache in sorted set (for replay)
  await redis.zadd(`events:${auctionId}`, eventId, event);

  // 2. Trim to last 1000 events
  await redis.zremrangebyrank(`events:${auctionId}`, 0, -1001);

  // 3. Set TTL (refresh on every write)
  await redis.expire(`events:${auctionId}`, 300);

  // 4. Publish for live delivery
  await redis.publish(`auction_${auctionId}`, event);
}
```

**Replay on reconnect:**

```javascript
async function replayMissedEvents(auctionId, lastEventId, res) {
  // ZRANGEBYSCORE: get all events with score > lastEventId
  const missed = await redis.zrangebyscore(
    `events:${auctionId}`,
    lastEventId + 1,   // exclusive of last seen
    '+inf'
  );

  for (const raw of missed) {
    const event = JSON.parse(raw);
    res.write(`id: ${event.id}\ndata: ${JSON.stringify(event.data)}\n\n`);
  }

  // Client is now caught up, continue with live events
}
```

```
                    REPLAY FLOW
═══════════════════════════════════════════════════════════════════════

Client (reconnects)        SSE Server                         Redis
      │                        │                                │
      │ GET /stream            │                                │
      │ Last-Event-ID: 102     │                                │
      │───────────────────────►│                                │
      │                        │                                │
      │                        │  ZRANGEBYSCORE                 │
      │                        │  events:auction_123            │
      │                        │  103 → +inf                    │
      │                        │───────────────────────────────►│
      │                        │                                │
      │                        │◄─ [{id:103,...}, {id:104,...},  │
      │                        │    {id:105,...}]               │
      │                        │                                │
      │◄── id:103\ndata:...\n\n│                                │
      │◄── id:104\ndata:...\n\n│                                │
      │◄── id:105\ndata:...\n\n│   (caught up!)                 │
      │                        │                                │
      │    ... live events ... │                                │
      │◄── id:106\ndata:...\n\n│  (from Pub/Sub, real-time)     │
```

---

### Scaling Concerns

#### 1. Fan-Out Amplification

Every message published to a Redis channel is copied to ALL subscribers. With 20 SSE server instances, one PUBLISH results in 20 deliveries from Redis.

```
                    FAN-OUT COST
═══════════════════════════════════════════════════════════════════════

  1 PUBLISH → Redis copies message to N subscribers

  Instances:   5   │  20   │   100
  ─────────────────┼───────┼────────
  Fan-out:     5x  │  20x  │   100x
  Redis CPU:   Low │  Med  │   High
  Redis Net:   Low │  Med  │   High ⚠️
```

**Mitigations:**
- Keep the number of SSE instances reasonable (horizontal scale, but not infinite)
- Use Redis Cluster to distribute channel load across shards
- Consider partitioning: `auction_123` only subscribed by instances that actually have connections for it (dynamic subscribe/unsubscribe)

#### 2. Message Loss: No Subscriber Listening

Redis Pub/Sub is ephemeral. If a message is published and NO subscriber is listening (e.g., all SSE servers restarted simultaneously), the message vanishes.

```
                    MESSAGE LOSS SCENARIO
═══════════════════════════════════════════════════════════════════════

  T+0s:  All 3 SSE servers crash (deploy, OOM, etc.)
  T+1s:  Bid Service publishes "bid $600" to auction_123
  T+2s:  Redis delivers to... nobody. Message GONE from Pub/Sub.
  T+5s:  SSE servers come back, re-subscribe.
  T+6s:  Clients reconnect with Last-Event-ID.

  ✅ Saved by the sorted set cache!
     The publish function wrote to BOTH Pub/Sub AND the sorted set.
     When clients reconnect, replay fetches from the sorted set.

  ❌ Without the sorted set cache:
     Message is permanently lost. Clients never see bid $600.
```

**This is why dual-write (Pub/Sub + Sorted Set) is critical.** Pub/Sub handles real-time, sorted sets handle durability.

#### 3. Redis Memory Pressure

If you have 10,000 active auctions each caching 1000 events, that's 10M entries in Redis. Each event is ~200 bytes → ~2GB of RAM just for replay caches.

```
  Auctions:    1,000  │  10,000  │  100,000
  Events each: 1,000  │   1,000  │    1,000
  Event size:  200B   │    200B  │     200B
  ──────────────────────────────────────────
  Total RAM:   ~200MB │    ~2GB  │    ~20GB ⚠️
```

**Mitigations:**
- Aggressive TTLs (5 min for fast auctions, 30s for chat)
- Smaller max cache size (100-500 events instead of 1000)
- Use Redis eviction policies (`allkeys-lru`) as a safety net
- For extreme scale: move replay cache to a separate Redis instance from the Pub/Sub instance

#### 4. Single Point of Failure

If Redis goes down, ALL SSE servers lose their broadcast mechanism. Live events stop flowing.

```
                    REDIS FAILURE IMPACT
═══════════════════════════════════════════════════════════════════════

  Redis DOWN:
  ┌──────────────┐     ┌──────────────┐     ┌──────────────┐
  │ SSE Server 1 │     │ SSE Server 2 │     │ SSE Server 3 │
  │              │     │              │     │              │
  │ Connections: │     │ Connections: │     │ Connections: │
  │ still open ✅│     │ still open ✅│     │ still open ✅│
  │              │     │              │     │              │
  │ Events:      │     │ Events:      │     │ Events:      │
  │ NONE flowing❌│     │ NONE flowing❌│     │ NONE flowing❌│
  └──────────────┘     └──────────────┘     └──────────────┘

  Clients stay connected but see no updates until Redis recovers.
  Heartbeats still work (server-local), so connections don't drop.
```

**Mitigations:**
- **Redis Sentinel** for automatic failover (promotes replica to primary)
- **Redis Cluster** for distribution + redundancy
- **Fallback polling**: If Redis subscription drops, SSE server can temporarily poll the database for new events (degraded but functional)
- **Health check endpoint**: Monitor Redis connectivity, trigger alerts

#### 5. Scaling Architecture Summary

```
                    PRODUCTION SETUP
═══════════════════════════════════════════════════════════════════════

                         ┌─────────────────────┐
                         │    Redis Cluster     │
                         │  (Primary + Replicas)│
                         │                      │
                         │  Pub/Sub: Primary    │
                         │  Sorted Sets: Any    │
                         │  node (read replicas)│
                         └──────────┬───────────┘
                                    │
              ┌─────────────────────┼─────────────────────┐
              │                     │                     │
              ▼                     ▼                     ▼
     ┌──────────────┐     ┌──────────────┐     ┌──────────────┐
     │ SSE Server 1 │     │ SSE Server 2 │     │ SSE Server 3 │
     │ Subscriber ✓ │     │ Subscriber ✓ │     │ Subscriber ✓ │
     │ 10K conns    │     │ 10K conns    │     │ 10K conns    │
     └──────────────┘     └──────────────┘     └──────────────┘
              ▲                     ▲                     ▲
              │                     │                     │
              └─────────────────────┼─────────────────────┘
                                    │
                         ┌──────────┴───────────┐
                         │    Load Balancer      │
                         │  (sticky sessions OR  │
                         │   any-instance OK)    │
                         └──────────────────────┘
                                    ▲
                                    │
                              30K clients
```

**Key insight:** SSE doesn't require sticky sessions. If a client reconnects to a different instance, `Last-Event-ID` + sorted set replay ensures they catch up seamlessly. Any instance can serve any client.
