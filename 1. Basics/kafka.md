# Kafka for System Design

## Table of Contents

1. [Core Concepts Refresher](#1-core-concepts-refresher)
2. [How to Partition — The Core Skill](#2-how-to-partition--the-core-skill)
   - 2.1 [What a Partition Key Actually Does](#21-what-a-partition-key-actually-does)
   - 2.2 [The Golden Rule for Picking a Partition Key](#22-the-golden-rule-for-picking-a-partition-key)
   - 2.3 [Common Interview Problems & Partition Key Decisions](#23-common-interview-problems--partition-key-decisions)
3. [Calculating Throughput & Number of Partitions](#3-calculating-throughput--number-of-partitions)
   - 3.1 [Throughput Formula](#31-throughput-formula)
   - 3.2 [Partition Count Formula](#32-partition-count-formula)
   - 3.3 [Worked Example: Ride-Sharing Event Stream](#33-worked-example-ride-sharing-event-stream)
   - 3.4 [Why Reads Per Consumer Group = Writes](#34-why-reads-per-consumer-group--writes)
   - 3.5 [Worked Example: Consumer Processing Constraint (Flink)](#35-worked-example-consumer-processing-constraint-flink)
4. [Hot Partitions — Detection & Solutions](#4-hot-partitions--detection--solutions)
5. [Scaling Kafka](#5-scaling-kafka)
6. [Handling Backpressure](#6-handling-backpressure)
7. [Consumer Lag — Identification & Remedies](#7-consumer-lag--identification--remedies)
   - 7.1 [Identifying Consumer Lag](#71-identifying-consumer-lag)
   - 7.2 [Remedies for Consumer Lag](#72-remedies-for-consumer-lag)
   - 7.3 [Lag Diagnosis Decision Tree](#73-lag-diagnosis-decision-tree)
8. [Persistence on AWS](#8-persistence-on-aws)
9. [Full System Design Example: Notification System](#9-full-system-design-example-notification-system)
10. [Full System Design Example: Uber Surge Pricing](#10-full-system-design-example-uber-surge-pricing)

---

## 1. Core Concepts Refresher

```
┌─────────────────────────────────────────────────────────────────────┐
│                        KAFKA TOPOLOGY                                │
│                                                                     │
│  Producer  ──►  Topic: "ride-events"                                │
│                 ┌────────────┬────────────┬────────────┐            │
│                 │ Partition 0│ Partition 1│ Partition 2│            │
│                 │ [msg1]     │ [msg2]     │ [msg3]     │            │
│                 │ [msg4]     │ [msg5]     │ [msg6]     │            │
│                 │ [msg7]     │            │            │            │
│                 └────────────┴────────────┴────────────┘            │
│                      ▲            ▲            ▲                    │
│                 Consumer Group: "surge-calculator"                   │
│                 ┌────────────┬────────────┬────────────┐            │
│                 │ Consumer 0 │ Consumer 1 │ Consumer 2 │            │
│                 │ reads P0   │ reads P1   │ reads P2   │            │
│                 └────────────┴────────────┴────────────┘            │
└─────────────────────────────────────────────────────────────────────┘
```

Key rules:
- **One partition → one consumer** in a consumer group at a time (guarantees ordering per partition)
- **Messages in a partition are ordered**. Across partitions, no global ordering.
- **More partitions = more parallelism**, but more overhead
- **Replication factor** = how many broker copies. Typically 3 in production.
- **Offset** = position of a message in a partition. Consumers track their own offset.

---

## 2. How to Partition — The Core Skill

### 2.1 What a Partition Key Actually Does

When a producer sends a message, Kafka routes it to a partition using:

```
partition = hash(key) % numPartitions
```

All messages with the **same key always land in the same partition**. This guarantees that a single consumer processes them **in order**.

```
Producer sends:
  key="driver_42", msg={status: "pickup"}   → hash → Partition 1
  key="driver_42", msg={status: "dropoff"}  → hash → Partition 1  ✅ ordered
  key="driver_99", msg={status: "pickup"}   → hash → Partition 0
  key="driver_42", msg={status: "arrived"}  → hash → Partition 1  ✅ ordered
```

Without a key (null), Kafka round-robins across partitions — no ordering guarantees.

### 2.2 The Golden Rule for Picking a Partition Key

Ask yourself two questions:

**1. What entity needs its events processed in order?**
If you're processing a user's payment history, events for the same user must be in order. The answer is your partition key.

**2. Is the key high-cardinality and evenly distributed?**
A key with only 3 possible values across 100 partitions means most partitions are empty. You want many distinct key values.

```
BAD key choices:
  - country code ("US", "UK", "IN") → only 3 values, hot partitions
  - boolean (true/false) → only 2 values
  - hour of day → only 24 values, uneven load

GOOD key choices:
  - userId, driverId, orderId, sessionId, auctionId
  - These have millions of distinct values → even distribution
```

**3. What is the unit of work that must not be parallelized?**
Two consumers must never process events for the same entity simultaneously (would cause race conditions). Your partition key is that entity's ID.

### 2.3 Common Interview Problems & Partition Key Decisions

#### Problem 1: Design Twitter / News Feed

```
Topic: "tweet-events"
Events: tweet_created, tweet_liked, tweet_retweeted, tweet_deleted

❌ Wrong key: tweetId
   Why: Downstream fan-out service needs to process all tweets from
        the same user together (e.g., enforcing rate limits, ordering
        on a user's timeline). Different tweets from same user could
        land on different consumers.

✅ Correct key: userId (the author)
   Why: All events for a given user's tweets go to the same partition.
        The fan-out consumer processes User A's activity in order.
        Rate limiting consumer sees a coherent stream per user.

Topic: "timeline-updates"  (fan-out results)
✅ Correct key: followerId
   Why: Each follower's timeline updates must be applied in order.
        follower_456 sees tweets in chronological order.
```

#### Problem 2: Design Uber / Ride-Sharing

```
Topic: "ride-events"
Events: ride_requested, driver_assigned, pickup, dropoff, payment

❌ Wrong key: eventType
   Why: Only ~5 distinct values. All "pickup" events on same partition.
        Doesn't relate to business ordering requirement.

✅ Correct key: rideId
   Why: The lifecycle of ONE ride must be processed in order.
        ride_requested → driver_assigned → pickup → dropoff → payment
        These must be seen in sequence by the billing/state machine consumer.

Topic: "driver-location-updates"
✅ Correct key: driverId
   Why: GPS pings for driver_42 must be ordered to compute trajectory.
        Surge pricing needs the latest location per driver.
        geofence detection consumer tracks each driver's movement.
```

#### Problem 3: Design a Stock Trading / Auction System

```
Topic: "order-events"
Events: order_placed, order_matched, order_cancelled, order_filled

✅ Correct key: stockSymbol (e.g., "AAPL", "TSLA")
   Why: The order book for AAPL must be updated sequentially.
        Two orders on AAPL must not be matched concurrently.
        All AAPL events go to same partition → single consumer processes them.

Alternative key: orderId
   When to use orderId instead:
   - If you want to track the lifecycle of ONE order (placed→matched→filled)
   - If matching is done upstream and you're just auditing/billing

Topic: "bid-events" (auction system)
✅ Correct key: auctionId
   Why: All bids for auction_123 must be ordered (first bid, second bid...).
        The winner-determination consumer sees them in sequence.
```

#### Problem 4: Design a Payment System

```
Topic: "payment-events"
Events: payment_initiated, fraud_check, payment_processed, refund_requested

✅ Correct key: userId (or accountId)
   Why: A user's payment events must be processed in order to:
        - Detect duplicate payments (same user initiated twice)
        - Enforce daily spend limits (need running total per user)
        - Fraud detection needs sequential context (normal → suspicious)

❌ Wrong key: merchantId
   Why: You'd get all payments to Amazon on one partition.
        High-cardinality merchants cause hot partitions.
        No ordering requirement at the merchant level for consumers.

Topic: "ledger-events"
✅ Correct key: accountId
   Why: Debits and credits on account_789 must be applied in order.
        Balance calculation is stateful and order-sensitive.
```

#### Problem 5: Design a Notification System

```
Topic: "notification-events"
Events: email_requested, sms_requested, push_requested

✅ Correct key: userId
   Why: Deduplication — if user_88 gets the same notification triggered
        twice, the consumer needs to see both events to deduplicate.
        Rate limiting — max 3 emails/hour per user needs ordered stream.

Topic: "delivery-status"
Events: delivered, bounced, opened

✅ Correct key: notificationId
   Why: The lifecycle of one notification (sent→delivered→opened) must
        be tracked in order to update the notification record.
```

#### Problem 6: Design a Chat System

```
Topic: "chat-messages"

❌ Wrong key: senderId
   Why: Messages from Alice to Bob and Alice to Carol go to same partition.
        Unnecessary ordering constraint. Different conversations are independent.

✅ Correct key: conversationId (or roomId)
   Why: Messages within a conversation must arrive in order.
        conversation_abc's messages: msg1 → msg2 → msg3 (sequence matters)
        Different conversations can be processed in parallel.
```

#### The Decision Framework (Use in Interviews)

```
Step 1: What is the "entity" whose state changes over time?
        → That entity's ID is your candidate key

Step 2: Do events for this entity need to be ordered?
        YES → Use entity ID as key
        NO  → Can use null (round-robin) or a different key

Step 3: Is this key high-cardinality and evenly distributed?
        YES → Good to go
        NO  → Reconsider (hot partition risk)

Step 4: What consumer needs to process this stream?
        → The consumer's "unit of work" confirms your key choice

Examples:
  Ordering matters for → rideId, orderId, conversationId, auctionId
  State machine      → one key = one state machine instance
  Rate limiting      → userId (need sequential view per user)
  Deduplication      → entity ID (need to see duplicates together)
```

#### The Trap: When Your Entity Count Exceeds Partition Limits

The decision framework above works until your entity cardinality vastly exceeds what Kafka can physically support as partitions. The Ad Click Aggregator is the canonical example of this.

**The naive answer (and why it breaks):**

You're designing an ad click aggregator. 10 million active AdIds per day. The obvious instinct is:

```
Partition key = AdId
→ All clicks for ad_42 go to same partition
→ Consumer sees them in order
→ Clean per-ad aggregation
```

The problem: you'd need up to 10 million partitions to ensure even distribution. Kafka partitions are **physical files on disk** — each is a directory of log segments, index files, and metadata. A broker managing 10,000 partitions is already heavy. 10 million partitions crashes the cluster.

```
Kafka partition = physical directory on broker disk

  /kafka-logs/
    ad-clicks-0/         ← partition 0
      00000000000.log
      00000000000.index
      00000000000.timeindex
    ad-clicks-1/         ← partition 1
      ...
    ad-clicks-9999999/   ← partition 9,999,999 ← 💥 OS file descriptor limit
      ...

  Practical limit: a few thousand partitions per cluster
  10 million partitions: immediate crash
```

**The correct mental model:**

```
Kafka layer  → partition by THROUGHPUT (how many partitions can I afford?)
Flink layer  → re-key by AdId (re-establish business logic per entity)
```

These are two separate concerns. Kafka's job is to move data reliably at scale. Flink's job is to apply business logic per entity.

```
                    AD CLICK AGGREGATOR FLOW
═══════════════════════════════════════════════════════════════════════

Producers (click servers)
  click: {adId: 7823451, userId: "u1", ts: ...}
  click: {adId: 0000042, userId: "u2", ts: ...}
  click: {adId: 7823451, userId: "u3", ts: ...}
        │
        │  Partition key: adId % numPartitions  (or just adId hash)
        ▼
Kafka Topic: "ad-clicks"  (200 partitions, driven by throughput math)

  Partition 0:  ad_42, ad_5001, ad_99234, ad_7823451  ← mixed AdIds
  Partition 1:  ad_17, ad_3388, ad_500211             ← mixed AdIds
  ...
  Partition 199: ad_88, ad_1024, ad_77777             ← mixed AdIds

  Multiple AdIds per partition — that's fine. Ordering is per-AdId
  within the partition (same AdId always lands same partition).
        │
        ▼
Flink Consumer (200 parallel tasks, one per partition)

  Each Flink task receives a mix of AdIds.

  task.keyBy(event -> event.adId)   ← RE-KEY by AdId inside Flink

  Flink's internal state backend now isolates state per AdId:
  ┌──────────────────────────────────────────────┐
  │  Flink Task (reads Partition 0)               │
  │                                              │
  │  keyBy(adId) creates internal keyed state:   │
  │    ad_42:      {clicks: 17, lastTs: ...}     │
  │    ad_5001:    {clicks: 3,  lastTs: ...}     │
  │    ad_99234:   {clicks: 44, lastTs: ...}     │
  │    ad_7823451: {clicks: 8,  lastTs: ...}     │
  └──────────────────────────────────────────────┘

  Each AdId's events are processed in order (Kafka guarantees
  same AdId → same partition → same Flink task → keyBy state).
        │
        ▼
  Tumbling window: every 1 minute, emit aggregated count per AdId
  → write to Redis / DynamoDB / Cassandra
```

**Why this works:**

Since `hash(adId) % 200` is deterministic, ad_42 always goes to the same Kafka partition, and always gets processed by the same Flink task. Within that task, `keyBy(adId)` maintains isolated state per ad. The ordering guarantee is preserved without needing a partition per AdId.

```
Key insight for interviews:
  Kafka partition count = f(throughput)       ← infrastructure constraint
  Flink keyBy          = f(business entity)   ← logic constraint

  These are independent. You solve them at different layers.
  Kafka moves the data. Flink understands it.
```

**Step 4 addition to the decision framework:**

```
Step 5: Does entity cardinality (number of unique keys) exceed
        your practical partition limit (~few thousand)?

        NO  → Use entity ID directly as partition key
        YES → Partition by hash bucket (adId % N), then
              re-key at the consumer layer (Flink keyBy,
              Kafka Streams groupBy, or application-level map)
```

---

## 3. Calculating Throughput & Number of Partitions

### 3.1 Throughput Formula

Start by estimating your raw numbers:

```
Write Throughput    = events/second × avg message size (bytes)
                    = X MB/s

Read Throughput     = Write Throughput × number of consumer groups
                    (each consumer group reads the full topic independently)

Consumer Processing = total EPS / consumer instance EPS capacity
Capacity              (only relevant when processing is expensive)
```

Single partition throughput limits (rough benchmarks on commodity hardware):
```
  Write: ~10 MB/s per partition (producer → broker)
  Read:  ~50 MB/s per partition (broker → consumer, sequential reads are fast)
```

These are conservative. MSK (AWS Managed Kafka) on larger instances can do 100+ MB/s per partition, but use 10/50 as safe interview estimates.

**When consumer processing capacity dominates:**

Kafka's I/O limits are generous — a single partition handles 10 MB/s writes and 50 MB/s reads. But if your consumer does expensive work (Flink aggregations, ML inference, DB writes), the processing rate per instance is the real bottleneck, not Kafka.

```
Example: Flink on commodity hardware processes 500 EPS per task.
         Producer emits 10,000 EPS at 1 KB each (10 MB/s).

  Kafka I/O constraint:  ceil(10 MB/s ÷ 10) = 1 partition needed
  Flink constraint:      ceil(10,000 ÷ 500) = 20 partitions needed  ← binding

  Kafka could serve this with 1-2 partitions.
  But your Flink cluster physically can't process 10,000 EPS with 1 task.
  You need 20 partitions to feed 20 parallel Flink tasks.
```

Rule of thumb:
```
  Processing is expensive (Flink, ML, DB writes) → consumer capacity dominates
  Processing is trivial (fan-out, passthrough)    → Kafka I/O dominates
```

### 3.2 Partition Count Formula

```
numPartitions = max(
  ceil(totalWriteMBps / 10),            ← broker write capacity
  ceil(totalReadMBps  / 50),            ← broker read capacity
  ceil(totalEPS       / consumerEPS)    ← consumer processing capacity
) × 2                                    ← growth headroom
```

Where:
- `totalReadMBps` = writeMBps × number of consumer groups
- `consumerEPS` = max events/second a single consumer instance can process
- Skip `consumerEPS` constraint if processing is trivial (simple fan-out, passthrough) — Kafka I/O limits will dominate

The result gives you the minimum partition count. Two additional checks before finalising:
- **Key cardinality**: partition count must be << number of distinct key values, otherwise some partitions get no data (e.g., 1000 partitions keyed by a field with only 50 values → 950 empty partitions)
- **Business isolation**: if your design requires one consumer per logical unit (one Flink task per city, one worker per tenant), bump partition count up to that number if it exceeds the formula result. This is a design decision, not a math constraint — mention it verbally in an interview rather than plugging it into the formula.

### 3.3 Worked Example: Ride-Sharing Event Stream

```
Given:
  - 500,000 active rides at peak
  - Each ride emits ~1 event every 10 seconds (GPS + status)
  - Average message size: 500 bytes
  - 3 consumer groups:
      1. Surge pricing calculator (Flink, 1,000 EPS per task)
      2. Billing service
      3. Analytics pipeline

Step 1: Events per second
  500,000 rides × (1 event / 10s) = 50,000 events/second

Step 2: Write throughput
  50,000 events/s × 500 bytes = 25 MB/s

Step 3: Read throughput
  3 consumer groups × 25 MB/s = 75 MB/s total reads

Step 4: Partitions needed for broker writes
  ceil(25 MB/s ÷ 10 MB/s per partition) = 3 partitions

Step 5: Partitions needed for broker reads
  ceil(75 MB/s ÷ 50 MB/s per partition) = 2 partitions

Step 6: Partitions needed for Flink processing capacity
  ceil(50,000 EPS ÷ 1,000 EPS per task) = 50 partitions  ← binding

Step 7: Desired consumer parallelism
  Surge pricing: 20 consumers for city regions
  → already covered by Step 6 (50 > 20)

Step 8: Apply growth headroom (2x)
  50 × 2 = 100 partitions

Final answer: 100 partitions
  Verify: 100 Flink tasks × 1,000 EPS = 100,000 EPS capacity >> 50,000 EPS ✅
          100 partitions × 10 MB/s = 1,000 MB/s write capacity >> 25 MB/s ✅
```

```
Quick Estimation Formula for Interviews:
═══════════════════════════════════════
  P = max(
    ceil(peakWriteMBps / 10),             ← write bound
    ceil(peakWriteMBps × CGcount / 50),   ← read bound
    ceil(totalEPS / consumerEPS)           ← processing bound
  ) × 2                                    ← growth headroom
```

---

## 3.4 Why Reads Per Consumer Group = Writes

This is the most misunderstood part of the throughput calculation, so it deserves its own section.

Kafka is a **log, not a queue**. When a consumer reads a message, the message is NOT removed. It stays in the partition until the retention period expires. Every consumer group reads the full topic independently from its own offset.

```
SQS / RabbitMQ (queue model):
  Producer writes 25 MB/s
  3 consumers split the work → each reads ~8.3 MB/s
  Message is deleted after one consumer reads it

Kafka (log model):
  Producer writes 25 MB/s
  Consumer Group 1 reads 25 MB/s  (full stream)
  Consumer Group 2 reads 25 MB/s  (full stream, independently)
  Consumer Group 3 reads 25 MB/s  (full stream, independently)
  Message stays in partition for 7 days regardless
```

Think of it like a newspaper. The printing press runs at a fixed rate. Every subscriber gets the full paper — one subscriber reading their copy doesn't prevent another from reading theirs. The press output rate IS the read rate per subscriber.

```
                    RIDE-SHARING EXAMPLE
═══════════════════════════════════════════════════════════════════════

Location Service produces:
  50,000 events/s × 500 bytes = 25 MB/s → Kafka brokers
                                               │
                    ┌──────────────────────────┤ same 25 MB/s
                    │                          │ sits on disk
                    ▼                          ▼
           ┌─────────────┐             ┌─────────────┐
           │  Partition 0│  ...        │ Partition 39│
           │ [e1][e2]... │             │ [e3][e5]... │
           └──────┬──────┘             └──────┬──────┘
                  │                           │
        ┌─────────┼─────────────────┐         │
        ▼         ▼                 ▼         ▼
  ┌──────────┐ ┌──────────┐ ┌──────────────────────┐
  │  Group 1 │ │  Group 2 │ │       Group 3        │
  │  Surge   │ │ Billing  │ │     Analytics        │
  │ 25 MB/s  │ │ 25 MB/s  │ │      25 MB/s         │
  └──────────┘ └──────────┘ └──────────────────────┘

  Broker total read load: 75 MB/s
  (same 25 MB/s served 3 times — once per group)
```

The broker's job is to serve every group the same data off disk. The read load it experiences is:

```
broker read load = write rate × number of consumer groups
                 = 25 MB/s × 3 = 75 MB/s
```

**The only exception — catch-up reads:**

If a consumer falls behind (lagging), it temporarily reads faster than the write rate to burn through the backlog:

```
Producer:       ──────────────── 25 MB/s ────────────────►
Lagging group:  ──────────────── 50 MB/s ────────────────► (catching up)
                                    ▲
                    Reads backlog faster than it was written
                    Settles back to 25 MB/s once caught up
```

This is why the read capacity per partition (~50 MB/s) is higher than write capacity (~10 MB/s). Sequential disk reads are cheap — Kafka is optimized for this. The headroom above your steady-state read throughput is what absorbs catch-up bursts.

---

## 3.5 Worked Example: Consumer Processing Constraint (Flink)

This example shows a case where consumer processing capacity — not Kafka I/O — is the binding constraint on partition count.

```
Given:
  - Producer emits 10,000 events/second
  - Average message size: 1 KB
  - Flink on commodity hardware: 500 EPS per task
  - 1 consumer group (Flink pipeline)

Step 1: Write throughput
  10,000 events/s × 1 KB = 10 MB/s

Step 2: Read throughput
  1 consumer group × 10 MB/s = 10 MB/s

Step 3: Partitions needed for broker writes
  ceil(10 MB/s ÷ 10 MB/s per partition) = 1 partition

Step 4: Partitions needed for broker reads
  ceil(10 MB/s ÷ 50 MB/s per partition) = 1 partition

Step 5: Partitions needed for Flink processing capacity
  ceil(10,000 EPS ÷ 500 EPS per task) = 20 partitions  ← binding constraint

Step 6: Apply growth headroom (2x)
  20 × 2 = 40 partitions

Final answer: 40 partitions
  Verify: 40 Flink tasks × 500 EPS = 20,000 EPS capacity >> 10,000 EPS ✅
```

Kafka's I/O said 1 partition would be enough. But 1 Flink task can only handle 500 EPS — it would drown at 10,000 EPS, building lag at 9,500 messages per second. The expensive processing is the real constraint here, not the broker.

```
What happens with only 1 partition (under-partitioned):

  Partition 0 → Flink Task 1: 10,000 EPS arriving, can do 500 → builds lag
                               Lag grows at 9,500 msgs/s
                               After 1 minute: 570,000 messages behind

What happens with 20 partitions (correctly partitioned):

  Partition 0  → Flink Task 1:  500 EPS arriving, can do 500 → ✅ keeping pace
  Partition 1  → Flink Task 2:  500 EPS arriving, can do 500 → ✅
  ...
  Partition 19 → Flink Task 20: 500 EPS arriving, can do 500 → ✅
```

---

## 4. Hot Partitions — Detection & Solutions

A hot partition is one that receives significantly more messages than others. It becomes the bottleneck — its consumer can't keep up while other consumers sit idle.

### How Hot Partitions Happen

```
Topic: "payment-events"
Partition key: merchantId

  Partition 0 → Amazon    → 40,000 msgs/s  ← 🔥 HOT
  Partition 1 → Etsy      → 200 msgs/s
  Partition 2 → Shopify   → 15,000 msgs/s  ← warm
  Partition 3 → LocalShop → 50 msgs/s
  Partition 4 → LocalShop → 30 msgs/s

  Amazon is a power user. One partition drowns, others starve.
```

Low-cardinality keys cause the same problem:

```
Topic: "user-events"
Partition key: country

  Partition 0 → "US" → 80% of all traffic  ← 🔥 HOT
  Partition 1 → "IN" → 12% of traffic
  Partition 2 → "UK" → 5% of traffic
  Partition 3 → other → 3% of traffic
```

### Detecting Hot Partitions

```
CloudWatch / MSK metrics to watch:

  BytesInPerSec per partition:
    Partition 0: 95 MB/s  ← way above average
    Partition 1: 3 MB/s
    Partition 2: 2 MB/s
    ...

  kafka_consumer_group_lag per partition:
    Partition 0: 2,400,000 messages behind  ← hot
    Partition 1: 0
    Partition 2: 0

  Any single partition at >>  (total throughput / numPartitions)
  is a hot partition.
```

### Solution 1: Salting (Add Entropy to the Key)

Salting means appending a suffix to the partition key to artificially increase its cardinality, spreading a hot entity across multiple partitions. It's the same concept as salting in database sharding.

```
Original key: "amazon"        → hash → always Partition 0

Salted key:   "amazon-0"     → hash → Partition 0
              "amazon-1"     → hash → Partition 7
              "amazon-2"     → hash → Partition 3
              "amazon-3"     → hash → Partition 12
```

Three salting strategies:

**Random salt** — pick a random suffix on each message. Maximum spread.
```javascript
const salt = Math.floor(Math.random() * 4);  // 0-3
const key = `${merchantId}-${salt}`;
```

**Round-robin salt** — increment a counter per key. Deterministic, even distribution.
```javascript
const salt = (saltCounters.get(merchantId) || 0) % 4;
saltCounters.set(merchantId, salt + 1);
const key = `${merchantId}-${salt}`;
```

**Time-based salt** — suffix is a time bucket. Events within the same window land together, useful when downstream aggregations are time-windowed.
```javascript
const salt = Math.floor(Date.now() / 60000) % 4;  // changes every minute
const key = `${merchantId}-${salt}`;
```

**Trade-off:** You lose global ordering for the hot entity's events across its salted partitions. Only use salting when ordering within a hot entity is not required (e.g., analytics events, metrics, counters).

```
                    BEFORE (hot partition)
  ┌────────┬────────┬────────┬────────┐
  │  P0    │  P1    │  P2    │  P3    │
  │ Amazon │ Etsy   │Shopify │  misc  │
  │ 🔥🔥🔥  │  ✅    │  ✅    │  ✅    │
  └────────┴────────┴────────┴────────┘

                    AFTER (composite key, 4 sub-partitions for Amazon)
  ┌────────┬────────┬────────┬────────┬────────┬────────┬────────┐
  │  P0    │  P1    │  P2    │  P3    │  P4    │  P5    │  P6    │
  │Amazon-0│Amazon-1│Amazon-2│Amazon-3│  Etsy  │Shopify │  misc  │
  │  ✅    │  ✅    │  ✅    │  ✅    │  ✅    │  ✅    │  ✅    │
  └────────┴────────┴────────┴────────┴────────┴────────┴────────┘
```

### Solution 2: Two-Level Key (Entity + Sub-Key)

When ordering within the hot entity still matters, split by a natural sub-key:

```
Original key: userId (user_42 sends 50,000 msgs/s — a bot/service account)

Two-level key: userId + sessionId
  user_42:session_A → Partition 3
  user_42:session_B → Partition 11
  user_42:session_C → Partition 7

Each session's events are ordered within their partition.
Different sessions process in parallel.
```

Another example — stock trading:
```
Original key: stockSymbol ("AAPL" → hot)

Two-level key: stockSymbol + orderType
  "AAPL:buy"  → Partition 2
  "AAPL:sell" → Partition 9

Buy orders and sell orders process in parallel.
Ordering within buy orders is preserved.
```

### Solution 3: Dedicated Topic for Hot Keys

Isolate the hot entity entirely into its own topic with more partitions:

```
Before:
  Topic "payment-events" (20 partitions) — Amazon drowns everyone

After:
  Topic "payment-events-amazon" (50 partitions) — Amazon gets dedicated resources
  Topic "payment-events-default" (20 partitions) — everyone else

Producer routing:
  if (merchantId === 'amazon') {
    producer.send('payment-events-amazon', key=orderId, ...);
  } else {
    producer.send('payment-events-default', key=merchantId, ...);
  }
```

Use case: Multi-tenant SaaS where one enterprise customer generates 80% of traffic. Give them their own topic/consumer group with dedicated resources (separate MSK cluster in extreme cases).

### Solution 4: Better Partition Key Choice

Sometimes a hot partition means you chose the wrong key entirely.

```
Problem: "user-activity" topic keyed by country
  "US" partition is always hot

Fix: Re-key by userId instead
  Millions of distinct userIds → perfectly even distribution
  Each user's events still ordered within their partition
  No single partition dominates
```

### Decision Tree for Hot Partitions

```
Hot partition detected
        │
        ▼
Is ordering required for the hot entity?
        │
   NO ──┼──► Use composite key with random suffix
        │     (amazon-0, amazon-1, amazon-2...)
        │
  YES ──┼──► Does the entity have a natural sub-key?
              │
         YES ──┼──► Use two-level key (entity + sub-key)
               │     (AAPL:buy, AAPL:sell)
               │
          NO ──┼──► Is it one specific tenant/entity causing the problem?
                     │
               YES ──┼──► Dedicated topic for that entity
                     │
                NO ──┼──► Re-examine partition key choice entirely
                          (you probably chose a low-cardinality key)
```

---

## 5. Scaling Kafka

### Scaling Producers

```
                    PRODUCER SCALING
═══════════════════════════════════════════════════════════════════════

  ① Increase producer instances (horizontal)
  ─────────────────────────────────────────
  Producer 1 ─┐
  Producer 2 ──┼──► Topic (40 partitions)
  Producer 3 ─┘

  Each producer has its own connection pool to brokers.
  Multiple producers writing same topic is fine — Kafka handles it.

  ② Tune producer batching (reduce RTTs)
  ──────────────────────────────────────
  linger.ms=5          // Wait 5ms to batch messages before sending
  batch.size=65536     // 64KB batch max
  compression.type=lz4 // Compress batches (~4x reduction in bytes)

  ③ Async producer with acks=1 (for non-critical streams)
     vs acks=all (for financial/critical streams)
```

### Scaling Consumers

```
                    CONSUMER SCALING
═══════════════════════════════════════════════════════════════════════

  Rule: consumer count ≤ partition count
  (extra consumers beyond partition count sit idle)

  BEFORE (underscaled):
  ┌────────────┬────────────┬────────────┬────────────┐
  │ Partition 0│ Partition 1│ Partition 2│ Partition 3│
  └─────┬──────┴─────┬──────┴─────┬──────┴─────┬──────┘
        └────────────┼────────────┘            │
                     ▼                         ▼
               Consumer A                Consumer B
               (reads P0, P1)            (reads P2, P3)
               ← OVERLOADED →

  AFTER (scaled out):
  ┌────────────┬────────────┬────────────┬────────────┐
  │ Partition 0│ Partition 1│ Partition 2│ Partition 3│
  └─────┬──────┴─────┬──────┴─────┬──────┴─────┬──────┘
        ▼            ▼            ▼            ▼
   Consumer A   Consumer B   Consumer C   Consumer D
   (reads P0)   (reads P1)   (reads P2)   (reads P3)

  Scale: add consumers up to partition count.
  Need more parallelism? First increase partitions, then add consumers.
```

### Scaling Brokers (AWS MSK)

```
                    BROKER SCALING ON AWS MSK
═══════════════════════════════════════════════════════════════════════

  Vertical scaling:
    MSK instance types: kafka.t3.small → kafka.m5.4xlarge → kafka.m5.24xlarge
    Upgrade: MSK supports online broker resize (no downtime)

  Horizontal scaling (add brokers):
    Cluster: [Broker 1] [Broker 2] [Broker 3]
                ↓ Add Broker 4, 5, 6
             [Broker 1] [Broker 2] [Broker 3] [Broker 4] [Broker 5] [Broker 6]

    After adding brokers, run partition reassignment to rebalance:
    kafka-reassign-partitions.sh --reassignment-json-file plan.json --execute

  Storage scaling:
    MSK supports EBS volume resizing online (up to 16TB per broker)
    Enable MSK auto-expand: storage auto-scales when > 50% used
```

### Partition Increase (with Caveats)

```
kafka-topics.sh --alter --topic ride-events --partitions 80

⚠️  WARNING: Increasing partitions BREAKS key ordering guarantees
    for existing keys because hash(key) % 40 ≠ hash(key) % 80

    Strategy:
    1. Create new topic with more partitions
    2. Produce to new topic going forward
    3. Consumer reads both old (drain) and new topic
    4. Decommission old topic after drain

    This is why you should over-provision partitions from the start.
```

---

## 6. Handling Backpressure

Backpressure happens when consumers can't keep up with producers. The consumer lag grows — Kafka's `consumer_lag` metric (offset difference between latest produced and last consumed).

```
                    CONSUMER LAG GROWTH
═══════════════════════════════════════════════════════════════════════

Producer rate:  50,000 msgs/s  ──────────────────────────────────────►
Consumer rate:  30,000 msgs/s  ──────────────────────────────────────►
                               ▲
                           Lag grows at 20,000 msgs/s
                           After 1 hour: 72M messages behind
```

### Strategy 1: Scale Out Consumers

The first response — add more consumer instances up to the partition count.

```
  Lag detected → add consumer instances via:
  - Auto Scaling Group (EC2)
  - ECS Service scaling
  - Kubernetes HPA (Horizontal Pod Autoscaler)

  Metric: kafka_consumer_group_lag > threshold → scale out
```

### Strategy 2: Slow Down Producers (Producer-Side Throttling)

```
  If consumers can't catch up even at max scale, slow the producer:

  Option A: Producer buffer blocks
    max.block.ms=5000          // Block producer for 5s if buffer full
    buffer.memory=33554432     // 32MB buffer before blocking

  Option B: Back-channel signal
  ┌──────────┐   lag too high    ┌────────────┐   slow down   ┌──────────┐
  │ Consumer │──────────────────►│ Monitoring │──────────────►│ Producer │
  │ (lag >1M)│                   │  (Lambda)  │   API call    │ service  │
  └──────────┘                   └────────────┘               └──────────┘

  Option C: Circuit breaker at API Gateway
    If Kafka lag > threshold, API Gateway returns 429 Too Many Requests
    Client retries with exponential backoff
```

### Strategy 3: Consumer-Side Processing Optimization

```
  ① Increase consumer parallelism within the instance
    fetch.min.bytes=65536       // Fetch 64KB at minimum (fewer fetch requests)
    fetch.max.wait.ms=500       // Wait 500ms to build a batch
    max.poll.records=1000       // Process 1000 msgs per poll loop

  ② Async processing (don't commit offset until batch is processed)
    consumer.poll() → buffer messages
    process in parallel threads
    commit offsets only after all in batch are done

  ③ Skip expensive work during catchup
    if (consumerLag > 100000) {
      // In catch-up mode: skip non-critical processing
      // Just write to DB, skip analytics/notifications
    }
```

### Strategy 4: Dead Letter Queue (DLQ) for Poison Pills

A single malformed message that causes a consumer to crash and restart creates an infinite loop.

```
                    POISON PILL PATTERN
═══════════════════════════════════════════════════════════════════════

Normal flow:
  Topic → Consumer → process ✅ → commit offset → next message

Poison pill:
  Topic → Consumer → process ❌ → retry 3x → still fails
                                      ↓
                              Send to DLQ topic: "ride-events-dlq"
                              Commit offset (skip the poison pill)
                              Continue processing next message

DLQ topic → Alert/monitoring → Manual inspection or replay after fix

AWS: Use SQS as DLQ, or a separate Kafka topic "topic-name-dlq"
```

```javascript
// Consumer with DLQ pattern
while (true) {
  const records = consumer.poll(Duration.ofMillis(100));
  for (const record of records) {
    let retries = 0;
    let processed = false;

    while (retries < 3 && !processed) {
      try {
        await processMessage(record);
        processed = true;
      } catch (err) {
        retries++;
        if (retries === 3) {
          await sendToDLQ(record, err); // Publish to DLQ topic
        }
      }
    }
  }
  consumer.commitSync(); // Commit after batch (including DLQ'd messages)
}
```

### Backpressure Monitoring on AWS

```
  CloudWatch Metrics to watch:
  ┌─────────────────────────────────────────────────────────┐
  │ Metric                          │ Alert Threshold        │
  │─────────────────────────────────┼────────────────────────│
  │ kafka_consumer_group_lag        │ > 100,000 messages     │
  │ BytesInPerSec (broker)          │ > 80% of capacity      │
  │ UnderReplicatedPartitions       │ > 0 (replication issue)│
  │ RequestHandlerAvgIdlePercent    │ < 20% (broker overload)│
  │ NetworkProcessorAvgIdlePercent  │ < 20% (network saturat)│
  └─────────────────────────────────────────────────────────┘
```

---

## 7. Consumer Lag — Identification & Remedies

Consumer lag is the gap between the latest message produced and the last message a consumer group has processed. It is the single most important health signal for a Kafka-based system.

```
Partition offset timeline:

  [0][1][2][3][4][5][6][7][8][9]  ← latest offset (produced)
                    ▲
                    consumer committed offset = 5

  Lag = latest offset − committed offset = 9 − 5 = 4 messages behind
```

---

### 7.1 Identifying Consumer Lag

#### Method 1: Kafka CLI

```bash
kafka-consumer-groups.sh \
  --bootstrap-server broker:9092 \
  --group surge-calculator \
  --describe

# Output:
GROUP             TOPIC        PARTITION  CURRENT-OFFSET  LOG-END-OFFSET  LAG
surge-calculator  ride-events  0          10450           10450           0
surge-calculator  ride-events  1          9823            11200           1377   ← lagging
surge-calculator  ride-events  2          8100            12900           4800   ← 🔥 hot
surge-calculator  ride-events  3          11001           11001           0
```

Key columns:
- `CURRENT-OFFSET` — last offset the consumer committed (what it has processed)
- `LOG-END-OFFSET` — latest offset produced (what exists in Kafka)
- `LAG` — the difference. Any non-zero value means the consumer is behind.

Per-partition view is critical — a zero total lag can hide one partition drowning while others are idle (hot partition problem).

#### Method 2: CloudWatch Metrics (AWS MSK)

```
MSK publishes these metrics automatically:

  Metric: kafka_consumer_group_lag
    Namespace: AWS/Kafka
    Dimensions: ClusterName, ConsumerGroup, Topic, Partition

  Metric: SumOffsetLag
    Sum of lag across all partitions for a consumer group
    → Good for alerting (total health)

  Metric: MaxOffsetLag
    Lag of the single most-behind partition
    → Good for detecting hot partitions
```

CloudWatch alarm setup:
```json
{
  "AlarmName": "KafkaConsumerLagHigh",
  "MetricName": "SumOffsetLag",
  "Namespace": "AWS/Kafka",
  "Dimensions": [
    { "Name": "ConsumerGroup", "Value": "surge-calculator" },
    { "Name": "Topic", "Value": "ride-events" }
  ],
  "Threshold": 100000,
  "ComparisonOperator": "GreaterThanThreshold",
  "EvaluationPeriods": 2,
  "Period": 60,
  "AlarmActions": ["arn:aws:sns:..."]
}
```

#### Method 3: Lag as Time (More Useful Than Message Count)

Raw message count is hard to reason about. Convert it to time:

```
lagMessages = 50,000
productionRate = 10,000 messages/second

lagTime = lagMessages / productionRate = 5 seconds behind
```

A lag of 1,000,000 messages sounds alarming, but if production rate is 500,000/s that's only 2 seconds. Conversely, 10,000 messages at 100/s is 100 seconds — actually worse.

```
lagTime = SumOffsetLag / (messages produced per second)
```

Alert on lag time, not raw message count. Aim for lag time < 30 seconds for real-time systems.

#### Lag Growth Rate — The Key Diagnostic

Lag at a point in time tells you the current state. **Lag growth rate** tells you whether the situation is improving or worsening:

```
T+0:   lag = 50,000    ← bad but stable?
T+60s: lag = 80,000    ← growing at 500 msgs/s → consumer can't keep up
T+60s: lag = 20,000    ← shrinking → consumer catching up, will self-heal

lagGrowthRate = (lag_now - lag_before) / time_interval

  Positive → consumer is slower than producer → need action
  Zero     → consumer keeping pace → stable
  Negative → consumer is catching up → monitor, likely self-heals
```

---

### 7.2 Remedies for Consumer Lag

Work through these in order — each step is cheaper than the next.

#### Remedy 1: Tune Consumer Fetch Settings

Before scaling anything, check if the consumer is leaving performance on the table with conservative defaults:

```
# Default settings (often too conservative)
fetch.min.bytes=1          # Fetch even 1 byte → many small fetch requests
max.poll.records=500       # Process 500 messages per poll loop
fetch.max.wait.ms=500      # Wait up to 500ms for fetch.min.bytes

# Tuned settings for high-throughput consumers
fetch.min.bytes=65536      # Wait for 64KB before responding → fewer, larger fetches
max.poll.records=2000      # Process 2000 messages per poll loop
fetch.max.wait.ms=100      # Don't wait too long, balance with fetch.min.bytes
```

Effect: reduces round-trip overhead, consumer spends more time processing and less time waiting for fetch responses. Often recovers 20-40% throughput for free.

#### Remedy 2: Optimize Consumer Processing

Profile what the consumer is actually doing per message:

```
Common culprits:
  ❌ Synchronous DB write per message
     Fix: Batch writes (accumulate 500 messages, write in one transaction)

  ❌ HTTP call per message (external API)
     Fix: Batch API calls, or async with concurrency limit

  ❌ Deserializing large Avro/Protobuf schemas on every message
     Fix: Cache the schema registry lookup

  ❌ No-op processing with logging overhead
     Fix: Reduce log verbosity in hot path

Before:  500 EPS per consumer instance
After:   2,000 EPS per consumer instance (just from batching DB writes)
→ Lag shrinks 4× faster without adding any infrastructure
```

#### Remedy 3: Scale Out Consumers (Up to Partition Count)

Add more consumer instances to the same consumer group. Kafka rebalances — each new consumer takes ownership of some partitions.

```
BEFORE (2 consumers, 8 partitions):
  Consumer A: reads P0, P1, P2, P3  → 4 partitions, overloaded
  Consumer B: reads P4, P5, P6, P7  → 4 partitions, overloaded

AFTER (8 consumers, 8 partitions):
  Consumer A: reads P0  → 1 partition ✅
  Consumer B: reads P1  → 1 partition ✅
  ...
  Consumer H: reads P7  → 1 partition ✅
```

Hard limit: you cannot have more consumers than partitions in a group. Extra consumers sit idle.

On AWS:
- **ECS**: increase `desiredCount` on the service
- **EC2 Auto Scaling**: increase `desiredCapacity`
- **Lambda**: increase `batchSize` and `maximumConcurrency` on the event source mapping
- **Flink on EMR/EKS**: increase task parallelism

#### Remedy 4: Increase Partitions + Scale Consumers

If you're already at one consumer per partition and lag is still growing, you've hit the ceiling. You need more partitions to allow more consumers.

```
Current state:
  8 partitions, 8 consumers, each processing 800 EPS
  Production rate: 10,000 EPS → 1,250 EPS per partition
  Consumer can only do 800 EPS per partition → lag growing at 450 EPS per partition

Fix:
  Increase to 20 partitions
  Add 20 consumer instances (500 EPS each)
  20 × 500 = 10,000 EPS capacity = matches production rate → lag stops growing
```

⚠️ Increasing partitions mid-flight breaks key ordering (see Section 3). Do this with a topic migration if ordering is critical.

#### Remedy 5: Reduce Message Size / Filter Early

If the consumer is reading a lot of data it doesn't need:

```
  Option A: Producer-side filtering
    Don't produce events the consumer won't use.
    Add a filter before the Kafka producer call.

  Option B: Topic splitting
    "all-events" topic (everything) → too noisy for some consumers
    Split into "payment-events", "location-events", "status-events"
    Consumer subscribes to only the topic it cares about

  Option C: Kafka Streams / Flink filter step
    Add a lightweight filter topology upstream
    Filtered topic has 10% of the original volume
    Downstream consumer only sees relevant events
```

#### Remedy 6: Temporarily Skip Non-Critical Work (Catch-Up Mode)

If lag is severe and growing, shed non-essential processing to catch up faster:

```javascript
const LAG_CATCHUP_THRESHOLD = 500_000;

async function processMessage(record, currentLag) {
  // Always do critical work
  await updateRideStatus(record);

  // Skip non-critical work during catch-up
  if (currentLag < LAG_CATCHUP_THRESHOLD) {
    await sendNotification(record);      // skip if lagging
    await updateAnalyticsDashboard(record); // skip if lagging
  }
}
```

Once lag drains back below threshold, full processing resumes automatically.

#### Remedy 7: Reset Offsets (Last Resort)

If lag is so severe it will take days to drain and the old data is stale/irrelevant:

```bash
# Skip to the latest offset — abandon the backlog entirely
kafka-consumer-groups.sh \
  --bootstrap-server broker:9092 \
  --group surge-calculator \
  --topic ride-events \
  --reset-offsets \
  --to-latest \
  --execute
```

⚠️ This permanently discards all unprocessed messages. Only valid when:
- Data is time-sensitive and stale data has no value (GPS locations from 2 hours ago)
- The consumer is non-critical (analytics, dashboards)
- You are NOT doing financial transactions, billing, or audit logging

---

### 7.3 Lag Diagnosis Decision Tree

```
Consumer lag detected (lagTime > 30s)
            │
            ▼
Is lag growing, stable, or shrinking?
            │
    Shrinking ──► Monitor. Will self-heal. No action needed.
            │
    Stable ─────► Consumer keeping pace but can't drain backlog.
            │      Apply Remedy 3 (scale out consumers).
            │
    Growing ─────────────────────────────────────────────────────┐
            │                                                     │
            ▼                                                     │
Is lag concentrated on specific partitions?                       │
            │                                                     │
           YES ──► Hot partition. See Section 3.5.               │
            │                                                     │
           NO ──► All partitions lagging equally                  │
            │                                                     │
            ▼                                                     │
Have consumer fetch settings been tuned?                          │
            │                                                     │
           NO ──► Apply Remedy 1. Re-check lag growth rate.      │
            │                                                     │
          YES ──►                                                  │
            │                                                     │
            ▼                                                     │
Is consumer doing expensive per-message work?                     │
            │                                                     │
          YES ──► Apply Remedy 2 (batch writes, cache schemas).  │
            │                                                     │
           NO ──►                                                  │
            │                                                     │
            ▼                                                     │
consumers < partitions?                                           │
            │                                                     │
          YES ──► Apply Remedy 3 (scale out consumers).          │
            │                                                     │
           NO ──► consumers = partitions, fully saturated        │
            │                                                     │
            ▼                                                     │
Apply Remedy 4: increase partitions + add consumers              │
If data is stale and non-critical → Remedy 7 (reset offsets)     │
└────────────────────────────────────────────────────────────────┘
```

---

## 8. Persistence on AWS

Kafka itself retains messages for a configurable duration (default 7 days). But for long-term storage, querying, and analytics, you need to persist to AWS storage.

### Architecture Overview

```
                    KAFKA PERSISTENCE PATTERNS ON AWS
═══════════════════════════════════════════════════════════════════════

Kafka Topic
    │
    ├──► S3 (via Kafka Connect S3 Sink or MSK Connect)
    │    Long-term cold storage, data lake, Athena queries
    │
    ├──► DynamoDB (via Lambda or Kafka Connect)
    │    Low-latency key-value lookups, current state
    │
    ├──► RDS / Aurora (via Lambda or Flink)
    │    Relational queries, transactional records
    │
    ├──► OpenSearch / Elasticsearch (via Kafka Connect)
    │    Full-text search, log analytics
    │
    ├──► Redshift (via Kafka Connect Redshift Sink)
    │    Data warehouse, complex analytics, BI dashboards
    │
    └──► ElastiCache / Redis (via Lambda)
         Hot data cache, leaderboards, session state
```

### Pattern 1: S3 — The Data Lake Sink

Best for: audit logs, analytics, replay, compliance, cheap long-term storage.

```
Kafka → MSK Connect (S3 Sink Connector) → S3 Bucket
                                            ├── year=2024/month=01/day=15/
                                            │     hour=10/
                                            │     ride-events-0-00001.parquet
                                            │     ride-events-1-00001.parquet
                                            └── (Hive-partitioned for Athena)
```

S3 Sink Connector config:
```json
{
  "name": "s3-sink-ride-events",
  "config": {
    "connector.class": "io.confluent.connect.s3.S3SinkConnector",
    "tasks.max": "10",
    "topics": "ride-events",
    "s3.region": "us-east-1",
    "s3.bucket.name": "my-data-lake",
    "s3.part.size": "67108864",
    "flush.size": "10000",
    "storage.class": "io.confluent.connect.s3.storage.S3Storage",
    "format.class": "io.confluent.connect.s3.format.parquet.ParquetFormat",
    "locale": "US",
    "timezone": "UTC",
    "timestamp.extractor": "RecordField",
    "timestamp.field": "eventTime",
    "partitioner.class": "io.confluent.connect.storage.partitioner.TimeBasedPartitioner",
    "path.format": "'year'=YYYY/'month'=MM/'day'=dd/'hour'=HH",
    "partition.duration.ms": "3600000"
  }
}
```

Then query with Athena:
```sql
SELECT rideId, eventType, COUNT(*)
FROM ride_events
WHERE year='2024' AND month='01' AND day='15'
GROUP BY rideId, eventType;
```

### Pattern 2: DynamoDB — Current State Store

Best for: maintaining the latest state of an entity from an event stream.

```
Kafka "ride-events" stream:
  {rideId: "r1", status: "requested",  ts: 100}
  {rideId: "r1", status: "assigned",   ts: 200}
  {rideId: "r1", status: "in_progress",ts: 300}

DynamoDB "rides" table:
  PK=r1 → {status: "in_progress", lastUpdated: 300, ...}
  (Consumer upserts on each event — only latest state survives)
```

```javascript
// Lambda consuming Kafka, writing current state to DynamoDB
exports.handler = async (event) => {
  const records = event.records;

  for (const [partition, msgs] of Object.entries(records)) {
    for (const msg of msgs) {
      const payload = JSON.parse(
        Buffer.from(msg.value, 'base64').toString()
      );

      await dynamoDB.put({
        TableName: 'rides',
        Item: {
          PK: payload.rideId,
          status: payload.status,
          lastUpdated: payload.timestamp,
          driverId: payload.driverId,
          // Conditional write: only update if this event is newer
        },
        ConditionExpression: 'attribute_not_exists(lastUpdated) OR lastUpdated < :ts',
        ExpressionAttributeValues: { ':ts': payload.timestamp }
      }).promise();
    }
  }
};
```

### Pattern 3: RDS/Aurora — Transactional Records

Best for: billing records, order history, anything needing SQL joins.

```
Kafka "payment-events" → Lambda → Aurora PostgreSQL

Lambda writes a record for each committed payment:
INSERT INTO payments (payment_id, user_id, amount, status, created_at)
VALUES (?, ?, ?, ?, ?)
ON CONFLICT (payment_id) DO NOTHING;  -- idempotent
```

Use **idempotent writes** (upsert with ON CONFLICT) because Kafka delivers at-least-once — your consumer might process the same message twice during rebalance.

### Pattern 4: Kinesis Data Firehose — Managed S3 Delivery

If you don't want to manage Kafka Connect, bridge Kafka → Firehose → S3:

```
Kafka → Lambda (consumer) → Kinesis Firehose → S3
                                  │
                            Firehose handles:
                            - Buffering (60s or 5MB)
                            - Compression (GZIP/Parquet)
                            - Partitioning by time
                            - Glue Data Catalog registration
                            - Athena-ready output
```

### Retention Strategy

```
                    TIERED RETENTION POLICY
═══════════════════════════════════════════════════════════════════════

  Kafka Broker (MSK):
    retention.ms = 604800000   // 7 days (hot, fast, replayable)
    retention.bytes = -1        // No size limit (use time-based)

  S3 (warm tier):
    Standard class: 30 days    // Recent analytics

  S3 (cold tier):
    S3 Infrequent Access: 90 days
    S3 Glacier: 1 year+         // Compliance / audit

  Lifecycle rule:
  ┌──────────────────────────────────────────────────────────┐
  │ Day 0-30:   S3 Standard                                  │
  │ Day 30-90:  S3 Standard-IA (auto-transition)             │
  │ Day 90+:    S3 Glacier Instant Retrieval (auto-transition)│
  │ Day 365+:   Delete (unless compliance requires longer)    │
  └──────────────────────────────────────────────────────────┘
```

---

## 9. Full System Design Example: Notification System

### Where Kafka Fits

```
Event Sources                 Kafka                    Notification Workers
─────────────                 ─────                    ────────────────────
Order Service   ──►┐                              ┌──► Email Worker
Payment Service ──►│  Topic: "notification-       │
Social Service  ──►│   requests"           ───────┤──► SMS Worker
Marketing       ──►┘                              │
                                                  └──► Push Worker
```

Kafka sits between event producers (anything that wants to notify a user) and the actual delivery workers. It decouples the two sides — a payment service shouldn't block waiting for SendGrid to respond, and shouldn't care how notifications are delivered.

Three specific problems Kafka solves here:

- **Rate limiting per user** — you don't want to send 50 notifications to a user in 5 minutes. The consumer enforces "max 3 notifications per user per hour" because all notifications for a user land on the same partition in order.
- **Deduplication** — if a payment event fires twice (at-least-once delivery), your consumer sees both events for the same user in sequence and can deduplicate before sending.
- **Retry without data loss** — if SendGrid is down, the notification stays in Kafka. The worker retries until it succeeds, then commits the offset.

### Partition Key: userId

```
The unit of work that must not be parallelized is the USER.

Why?
  - Rate limiting is per user → need to see all notifications for user_88
    in sequence to enforce "max 3 emails/hour"
  - Deduplication is per user → need to see duplicates together
  - Preference enforcement → user_88 opted out of SMS, need to check
    before sending → state is per user

  Two consumers must never process notifications for the same user
  simultaneously → userId as partition key ensures one consumer owns
  one user's stream.
```

### How Many Partitions?

#### P95/P99: Sizing for the Right Traffic Level

Before calculating partitions, you need to decide what traffic level to size for. Traffic is not constant — it spikes. The question is how big a spike to provision for statically vs handle with auto-scaling.

**Percentiles** describe the distribution of your traffic over time. If you record notifications-per-second every minute for a day, sort those 1,440 readings low-to-high:

```
Sorted traffic readings (notifications/second):
  P50  →  2,000/s   (normal Tuesday — 50% of time below this)
  P95  → 50,000/s   (marketing campaign fires — 95% of time below this)
  P99  → 150,000/s  (major product launch — 99% of time below this)
  P100 → 500,000/s  (Black Friday peak — absolute max, minutes per year)
```

Design rule:
```
  Static capacity (partitions, base consumer count) → size for P95 or P99
  Auto-scaling                                      → covers P99 → peak burst
  Rate limiting / shedding                          → extreme spikes beyond auto-scale

  Sizing for P100 means 10× infrastructure sitting idle 99.9% of the time.
  In interviews: "I'll size for P95 and rely on auto-scaling for short bursts."
```

#### Partition Calculation (P95 = 50,000 notifications/s)

```
Step 1: Write throughput
  Avg notification payload: ~2 KB (userId, template, metadata)
  50,000 events/s × 2 KB = 100 MB/s

Step 2: Read throughput
  Separate consumer groups per channel:
    - Email worker group
    - SMS worker group
    - Push worker group
    = 3 consumer groups

  100 MB/s × 3 = 300 MB/s total reads

Step 3: Partitions for broker writes
  ceil(100 / 10) = 10 partitions

Step 4: Partitions for broker reads
  ceil(300 / 50) = 6 partitions

Step 5: Consumer processing capacity  ← binding constraint
  Email worker calls SendGrid API: ~200 emails/s per worker instance
  ceil(50,000 / 200) = 250 partitions

Step 6: 2× growth headroom
  250 × 2 = 500 partitions

Final: 500 partitions
```

The external API call to SendGrid/Twilio is what drives the partition count, not Kafka I/O. Same pattern as the Flink example — expensive downstream work dominates.

### Topic Per Channel (Recommended over Single Topic)

```
Option A: Single topic, workers filter by channel
  Topic: "notification-requests"
  Partition key: userId
  Workers: each consumer group reads everything, skips what's not theirs

  Con: Email worker reads SMS events it doesn't need.
       Wastes read throughput. Can't size partitions independently.

Option B: Topic per channel ✅
  Topic: "email-notifications"     → Email worker group
  Topic: "sms-notifications"       → SMS worker group
  Topic: "push-notifications"      → Push worker group

  Pro: Each worker only reads its own topic.
       Push volume (10× SMS) gets its own independently-sized partition count.
       Simpler consumer logic — no filtering needed.
```

### Full Architecture

```
User triggers event
        │
        ▼
Notification Orchestrator
  - decides channels (email? sms? push?)
  - hydrates template
  - produces to per-channel Kafka topics
  - partition key = userId
        │
        ├──► "email-notifications"  (500 partitions)
        │         │
        │         └──► Email Workers (500 instances, auto-scaled)
        │               rate-limit check → deduplicate → SendGrid API
        │               DLQ → "email-notifications-dlq" on failure
        │
        ├──► "sms-notifications"    (100 partitions)
        │         │
        │         └──► SMS Workers (100 instances, auto-scaled)
        │               rate-limit check → deduplicate → Twilio API
        │
        └──► "push-notifications"   (1000 partitions)
                  │
                  └──► Push Workers (1000 instances, auto-scaled)
                        rate-limit check → deduplicate → FCM/APNs
```

Partition counts differ per topic because delivery rate per channel differs:
- Push (FCM/APNs) is fast — workers can handle more EPS → fewer partitions needed per worker
- Email (SendGrid) is slower due to API rate limits → more partitions needed to parallelize

### Persistence on AWS

```
  "notification-requests" → S3 (MSK Connect S3 Sink)
    Audit trail: who was notified, when, for what event

  "email-notifications-dlq" → SQS DLQ or Kafka DLQ topic
    Failed deliveries for manual inspection / retry

  Worker state (rate limiting, dedup) → ElastiCache Redis
    Key: "ratelimit:user_88:email" → TTL 1 hour, count = 3
    Key: "dedup:notif_abc123"      → TTL 24 hours, boolean
```

---

## 10. Full System Design Example: Uber Surge Pricing

Putting it all together in one realistic system design.

```
                    SURGE PRICING SYSTEM
═══════════════════════════════════════════════════════════════════════

  Mobile Apps (drivers/riders)
        │
        ▼
  API Gateway + NLB
        │
        ▼
  ┌─────────────────┐
  │  Location       │  GPS ping every 5s from 500K drivers at peak
  │  Service        │  = 100K events/second
  └────────┬────────┘
           │ produce
           ▼
  ┌─────────────────────────────────────────────────────┐
  │  Kafka Topic: "driver-location"                      │
  │  Partition key: driverId                             │
  │  Partitions: 100 (calc below)                        │
  │  Retention: 1 hour (GPS data stale quickly)          │
  └──────┬──────────────────┬───────────────────────────┘
         │                  │
         ▼                  ▼
  ┌────────────┐     ┌─────────────────┐
  │  Surge     │     │  S3 Sink        │
  │  Calculator│     │  (MSK Connect)  │
  │  (Flink /  │     │  ↓              │
  │   Lambda)  │     │  S3 Data Lake   │
  └─────┬──────┘     └─────────────────┘
        │
        │ Tumbling window: 5 min, by geohash cell
        │ Count drivers and ride requests per cell
        │ surgeMultiplier = rideRequests / availableDrivers
        │
        ▼
  Kafka Topic: "surge-updates"
  Partition key: geohashCellId
  Partitions: 50
        │
        ├──► Lambda → DynamoDB (current surge per cell)
        │            (riders query this for price estimate)
        │
        └──► Lambda → ElastiCache Redis (hot cache, <1ms reads)


  Throughput Calculation:
  ──────────────────────
  100,000 events/s × 200 bytes (GPS payload) = 20 MB/s writes

  Consumer groups: 3 (Surge Calculator, S3 Sink, Analytics)
  Read throughput: 3 × 20 MB/s = 60 MB/s

  Partitions needed:
    Write: ceil(20 / 10) = 2 partitions
    Read:  ceil(60 / 50) = 2 partitions
    Parallelism: 100 cities × surge calculators = 100 consumers
    With 2x headroom: 100 × 2 = 200 partitions

  Final: 200 partitions for "driver-location"
  (Key space: 500K driverIds >> 200 partitions → even distribution ✅)


  Backpressure Handling:
  ─────────────────────
  If surge calculator lags (e.g., complex geofence queries):
    1. CloudWatch alarm: consumer_lag > 500K
    2. Auto Scaling adds more Lambda concurrency (up to 200)
    3. Each Lambda reads 1 partition
    4. If still lagging: increase partition count (offline migration)


  Persistence:
  ────────────
  driver-location → S3 (Parquet, partitioned by hour)
                  → Athena queries for historical driver supply analysis

  surge-updates   → DynamoDB (current surge multiplier per geohash)
                  → Redis (hot cache for rider app price display)
                  → S3 → Redshift (historical surge analysis for ML)
```

---

## Quick Reference Card

```
┌──────────────────────────────────────────────────────────────────────┐
│                    KAFKA SYSTEM DESIGN CHEATSHEET                     │
├──────────────────────────────────────────────────────────────────────┤
│ PARTITION KEY SELECTION                                               │
│  Ask: "What entity needs ordered processing?"                         │
│  Use: That entity's ID (userId, rideId, orderId, auctionId)          │
│  Avoid: Low-cardinality keys (country, boolean, eventType)            │
│                                                                      │
│ PARTITION COUNT                                                       │
│  P = max(writes/10MB, reads×CG/50MB, consumerCount) × 2             │
│  Always over-provision — increasing partitions later is painful       │
│                                                                      │
│ SCALING                                                               │
│  Consumers: scale up to partition count                               │
│  Need more: increase partitions first, then consumers                 │
│  Brokers: MSK supports online vertical resize + add brokers           │
│                                                                      │
│ BACKPRESSURE                                                          │
│  1. Add consumers (up to partition count)                             │
│  2. Tune fetch batching (fetch.min.bytes, max.poll.records)          │
│  3. Throttle producers (max.block.ms, or API Gateway 429)            │
│  4. DLQ for poison pills (never block on bad message)                 │
│                                                                      │
│ PERSISTENCE ON AWS                                                    │
│  Current state   → DynamoDB (upsert, idempotent)                     │
│  Cold storage    → S3 + Athena (MSK Connect S3 Sink, Parquet)        │
│  Relational      → Aurora (Lambda consumer, ON CONFLICT DO NOTHING)  │
│  Analytics       → Redshift (Kafka Connect Redshift Sink)            │
│  Hot cache       → ElastiCache Redis (Lambda fan-out)                │
│  Search          → OpenSearch (Kafka Connect sink)                   │
└──────────────────────────────────────────────────────────────────────┘
```
