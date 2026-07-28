# Uploading Large Files Pattern (>50GB to S3)

## Architecture Overview

```
Client → API Gateway → Upload Service → S3 (Multipart Upload)
                              ↓
                        Database (Postgres RDS / DynamoDB)
                        (metadata + S3 link)
```

## Components

- **Client** - Initiates the upload, handles chunking on the client side
- **API Gateway** - Entry point, routes requests to Upload Service
- **Upload Service** - Orchestrates the upload flow, generates presigned URLs, tracks metadata
- **S3** - Final storage destination using Multipart Upload
- **Database** - Stores file metadata and the final S3 object link

---

## Detailed Flow

### Step 1: Client Requests Upload Initiation

The client sends a request to begin an upload, providing file metadata.

**Request:**
```
POST /api/uploads/initiate
Headers: Authorization: Bearer <token>

Body:
{
  "fileName": "large-dataset.zip",
  "fileSize": 53687091200,   // ~50GB in bytes
  "contentType": "application/zip",
  "checksum": "sha256:abc123...",
  "chunkSize": 104857600     // 100MB per chunk (client-proposed)
}
```

### Step 2: Upload Service Initiates S3 Multipart Upload

The Upload Service calls S3's `CreateMultipartUpload` API.

```python
import boto3

s3 = boto3.client('s3')

response = s3.create_multipart_upload(
    Bucket='my-uploads-bucket',
    Key=f'uploads/{user_id}/{upload_id}/{file_name}',
    ContentType=content_type,
    Metadata={
        'original-name': file_name,
        'uploader': user_id
    }
)

upload_id = response['UploadId']
```

### Step 3: Store Upload Metadata in Database

The Upload Service persists the upload record.

**Postgres RDS Schema:**
```sql
CREATE TABLE file_uploads (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL,
    file_name VARCHAR(1024) NOT NULL,
    file_size BIGINT NOT NULL,
    content_type VARCHAR(255),
    checksum VARCHAR(512),
    s3_bucket VARCHAR(255) NOT NULL,
    s3_key VARCHAR(1024) NOT NULL,
    s3_upload_id VARCHAR(512) NOT NULL,
    status VARCHAR(50) NOT NULL DEFAULT 'initiated',  -- initiated | uploading | completed | failed | aborted
    total_parts INT NOT NULL,
    uploaded_parts INT NOT NULL DEFAULT 0,
    created_at TIMESTAMP DEFAULT NOW(),
    completed_at TIMESTAMP,
    s3_url TEXT  -- populated after completion
);

CREATE TABLE upload_parts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    upload_id UUID REFERENCES file_uploads(id),
    part_number INT NOT NULL,
    etag VARCHAR(512),       -- returned by S3 after part upload
    size BIGINT NOT NULL,
    status VARCHAR(50) NOT NULL DEFAULT 'pending',  -- pending | uploaded | failed
    uploaded_at TIMESTAMP
);
```

**DynamoDB Alternative:**
```json
{
  "PK": "UPLOAD#<upload_id>",
  "SK": "METADATA",
  "userId": "<user_id>",
  "fileName": "large-dataset.zip",
  "fileSize": 53687091200,
  "s3Bucket": "my-uploads-bucket",
  "s3Key": "uploads/<user_id>/<upload_id>/large-dataset.zip",
  "s3UploadId": "<multipart-upload-id>",
  "status": "initiated",
  "totalParts": 512,
  "uploadedParts": 0,
  "createdAt": "2024-01-15T10:00:00Z",
  "TTL": 1705485600
}
```

Parts tracked as separate items:
```json
{
  "PK": "UPLOAD#<upload_id>",
  "SK": "PART#001",
  "partNumber": 1,
  "etag": null,
  "status": "pending"
}
```

### Step 4: Generate Presigned URLs for Each Part

The Upload Service generates presigned URLs so the client can upload directly to S3, bypassing API Gateway for the heavy data transfer.

```python
def generate_presigned_urls(bucket, key, upload_id, total_parts, expiry=3600):
    urls = []
    for part_number in range(1, total_parts + 1):
        url = s3.generate_presigned_url(
            'upload_part',
            Params={
                'Bucket': bucket,
                'Key': key,
                'UploadId': upload_id,
                'PartNumber': part_number
            },
            ExpiresIn=expiry  # 1 hour default
        )
        urls.append({
            'partNumber': part_number,
            'presignedUrl': url
        })
    return urls
```

**Response to Client:**
```json
{
  "uploadId": "app-level-upload-id",
  "s3UploadId": "s3-multipart-upload-id",
  "totalParts": 512,
  "chunkSize": 104857600,
  "presignedUrls": [
    { "partNumber": 1, "url": "https://my-uploads-bucket.s3.amazonaws.com/...?X-Amz-Signature=..." },
    { "partNumber": 2, "url": "https://my-uploads-bucket.s3.amazonaws.com/...?X-Amz-Signature=..." }
  ]
}
```

> **Note:** For 50GB+ files with 100MB chunks, that's ~512 parts. You may want to return URLs in batches rather than all at once (e.g., 50 at a time) to avoid URL expiration before the client reaches later parts.

### Step 5: Client Uploads Parts Directly to S3

The client uploads each chunk directly to S3 using the presigned URLs. This bypasses API Gateway entirely for the actual data transfer (API Gateway has a 10MB payload limit anyway).

```javascript
async function uploadParts(file, presignedUrls, chunkSize) {
  const results = [];
  const concurrency = 5; // parallel uploads

  for (let batch of chunk(presignedUrls, concurrency)) {
    const promises = batch.map(async ({ partNumber, url }) => {
      const start = (partNumber - 1) * chunkSize;
      const end = Math.min(start + chunkSize, file.size);
      const blob = file.slice(start, end);

      const response = await fetch(url, {
        method: 'PUT',
        body: blob,
        headers: { 'Content-Type': 'application/octet-stream' }
      });

      const etag = response.headers.get('ETag');
      return { partNumber, etag };
    });

    const batchResults = await Promise.all(promises);
    results.push(...batchResults);

    // Report progress back to Upload Service
    await reportProgress(batchResults);
  }

  return results;
}
```

### Step 6: Client Reports Part Completion

After each part (or batch), the client notifies the Upload Service.

```
POST /api/uploads/{uploadId}/parts
Body:
{
  "parts": [
    { "partNumber": 1, "etag": "\"abc123def456\"" },
    { "partNumber": 2, "etag": "\"789ghi012jkl\"" }
  ]
}
```

The Upload Service updates the database:
```sql
UPDATE upload_parts
SET etag = $1, status = 'uploaded', uploaded_at = NOW()
WHERE upload_id = $2 AND part_number = $3;

UPDATE file_uploads
SET uploaded_parts = uploaded_parts + $1, status = 'uploading'
WHERE id = $2;
```

### Step 7: Complete Multipart Upload

Once all parts are uploaded, the client (or Upload Service) calls complete.

```
POST /api/uploads/{uploadId}/complete
```

The Upload Service calls S3's `CompleteMultipartUpload`:

```python
def complete_upload(bucket, key, upload_id, parts):
    # parts = [{'PartNumber': 1, 'ETag': '"abc..."'}, ...]
    response = s3.complete_multipart_upload(
        Bucket=bucket,
        Key=key,
        UploadId=upload_id,
        MultipartUpload={'Parts': parts}
    )

    s3_url = f"s3://{bucket}/{key}"

    # Update database
    update_upload_status(upload_id, 'completed', s3_url)

    return {
        'status': 'completed',
        's3Url': s3_url,
        'location': response['Location']
    }
```

### Step 8: Update Final Metadata

```sql
UPDATE file_uploads
SET status = 'completed',
    completed_at = NOW(),
    s3_url = $1
WHERE id = $2;
```

---

## Presigned URL Security Details

| Concern | Solution |
|---------|----------|
| Authentication | Presigned URLs are generated only for authenticated users (JWT validated at API Gateway) |
| Expiration | URLs expire after a configured TTL (e.g., 1 hour per part) |
| Scoped permissions | The IAM role used to sign URLs has minimal permissions (`s3:PutObject` on specific prefix only) |
| One-time use | Each URL is scoped to a specific part number and upload ID |
| Content restrictions | Optionally enforce content-length conditions in the presigned URL |

**IAM Policy for Presigned URL Generation:**
```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": ["s3:PutObject"],
      "Resource": "arn:aws:s3:::my-uploads-bucket/uploads/${aws:userid}/*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "s3:CreateMultipartUpload",
        "s3:CompleteMultipartUpload",
        "s3:AbortMultipartUpload",
        "s3:ListMultipartUploadParts"
      ],
      "Resource": "arn:aws:s3:::my-uploads-bucket/uploads/*"
    }
  ]
}
```

---

## S3 Multipart Upload Constraints

| Parameter | Value |
|-----------|-------|
| Minimum part size | 5MB (except last part) |
| Maximum part size | 5GB |
| Maximum parts per upload | 10,000 |
| Maximum object size | 5TB |
| Recommended chunk for 50GB | 100MB (gives ~512 parts) |

---

## Error Handling & Resilience

### Retry Failed Parts
```javascript
async function uploadWithRetry(url, blob, maxRetries = 3) {
  for (let attempt = 1; attempt <= maxRetries; attempt++) {
    try {
      const response = await fetch(url, { method: 'PUT', body: blob });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return response.headers.get('ETag');
    } catch (err) {
      if (attempt === maxRetries) throw err;
      await sleep(Math.pow(2, attempt) * 1000); // exponential backoff
    }
  }
}
```

### Abort Stale Uploads
Set up an S3 lifecycle rule to auto-abort incomplete multipart uploads:
```json
{
  "Rules": [
    {
      "ID": "AbortIncompleteMultipartUploads",
      "Status": "Enabled",
      "AbortIncompleteMultipartUpload": {
        "DaysAfterInitiation": 7
      }
    }
  ]
}
```

### Resume Interrupted Uploads
Since the database tracks which parts are complete, the client can request only the remaining presigned URLs:
```
GET /api/uploads/{uploadId}/remaining-parts
```

---

## Sequence Diagram

```
Client              API Gateway          Upload Service              S3                  Database
  |                      |                      |                     |                      |
  |--- POST /initiate -->|--------------------->|                     |                      |
  |                      |                      |-- CreateMultipart ->|                      |
  |                      |                      |<-- uploadId --------|                      |
  |                      |                      |-- INSERT metadata --|--------------------->|
  |                      |                      |-- Generate URLs --->|                      |
  |<---- presigned URLs -|<---------------------|                     |                      |
  |                      |                      |                     |                      |
  |===== PUT part 1 (direct to S3) ==================================>|                      |
  |<==== ETag ========================================================|                      |
  |===== PUT part 2 (direct to S3) ==================================>|                      |
  |<==== ETag ========================================================|                      |
  |                      |                      |                     |                      |
  |--- POST /parts ----->|--------------------->|-- UPDATE parts -----|--------------------->|
  |                      |                      |                     |                      |
  |  ... (repeat for all parts) ...             |                     |                      |
  |                      |                      |                     |                      |
  |--- POST /complete -->|--------------------->|-- CompleteMPU ----->|                      |
  |                      |                      |<-- S3 URL ----------|                      |
  |                      |                      |-- UPDATE status ----|--------------------->|
  |<---- 200 OK ---------|<---------------------|                     |                      |
```

---

## API Summary

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/uploads/initiate` | POST | Start a new upload, get presigned URLs |
| `/api/uploads/{id}/presigned-urls` | GET | Get (more) presigned URLs for remaining parts |
| `/api/uploads/{id}/parts` | POST | Report completed parts (ETags) |
| `/api/uploads/{id}/complete` | POST | Finalize the multipart upload |
| `/api/uploads/{id}/abort` | POST | Cancel and clean up an upload |
| `/api/uploads/{id}/status` | GET | Check upload progress |

---

## Key Design Decisions

1. **Client uploads directly to S3** - API Gateway has a 10MB payload limit and would be a bottleneck. Presigned URLs let the client bypass it for data transfer while keeping the control plane secured.

2. **Chunking at 100MB** - Balances between number of parts (staying well under 10,000 limit) and individual request reliability. Smaller chunks mean easier retries.

3. **Batch presigned URL generation** - For very large files, generate URLs in batches to prevent expiration before use.

4. **Database tracks all state** - Enables resume, progress reporting, and audit trail. If the client disconnects, it can reconnect and continue from where it left off.

5. **S3 lifecycle rules** - Automatically clean up abandoned multipart uploads to avoid storage costs from orphaned parts.

---

## Resumable Uploads (Deep Dive)

### Why Resumability Matters

For a 50GB file on a typical connection, upload can take hours. Network drops, browser crashes, or laptop sleeps are inevitable. Without resumability, the user restarts from zero — wasting time and bandwidth.

### How It Works

The `upload_parts` table is the enabler. Each part has a status (`pending` | `uploaded` | `failed`), so at any point the system knows exactly what's done and what's left.

### Resumable Upload Flow Diagram

```
                        INITIAL UPLOAD (parts 1-5 succeed, then connection drops)
                        =========================================================

Client                          Upload Service                         Database
  |                                    |                                   |
  |-- Upload parts 1,2,3,4,5 --------->|                                   |
  |-- Report ETags for 1-5 ----------->|-- UPDATE parts 1-5 = 'uploaded' ->|
  |                                    |                                   |
  |  ❌ CONNECTION DROPS AT PART 6     |                                   |
  |                                    |                                   |


                        RESUME (client reconnects minutes/hours later)
                        ==============================================

Client                          Upload Service                         Database
  |                                    |                                    |
  |-- GET /uploads/{id}/status ------->|-- SELECT status, uploaded_parts -->|
  |                                    |<-- 5/512 uploaded, status=active --|
  |<-- { uploaded: 5, total: 512 } ----|                                    |
  |                                    |                                    |
  |-- GET /uploads/{id}/remaining ---->|-- SELECT part_number               |
  |    parts                           |   FROM upload_parts                |
  |                                    |   WHERE status != 'uploaded' ----->|
  |                                    |<-- parts 6,7,8,...,512 ------------|
  |                                    |                                    |
  |                                    |-- Generate presigned URLs          |
  |                                    |   for parts 6-512 only             |
  |<-- { presignedUrls: [6..512] } ----|                                    |
  |                                    |                                    |
  |== PUT part 6 (direct to S3) ====>                                       |
  |== PUT part 7 (direct to S3) ====>                                       |
  |  ... continues from where it left off ...                               |
  |                                    |                                    |
  |-- POST /complete ----------------->|-- CompleteMPU (all 512 ETags) ---->|
  |<-- 200 OK -------------------------|                                    |
```

### Key Points

- **No re-upload of completed parts** — Parts 1-5 already exist in S3 as uploaded segments. They don't expire as long as the multipart upload hasn't been aborted.
- **Fresh presigned URLs** — Old URLs may have expired, so the service generates new ones only for remaining parts.
- **Idempotent part upload** — Uploading the same part number again just overwrites it in S3, no harm done.
- **Timeout protection** — If an upload has been idle for too long (e.g., 7 days), the S3 lifecycle rule aborts it and the service marks it as `expired`.

### Client-Side Resume Logic

```javascript
async function resumeUpload(uploadId) {
  // 1. Ask server what's remaining
  const status = await fetch(`/api/uploads/${uploadId}/status`);
  const { uploadedParts, totalParts } = await status.json();

  if (uploadedParts === totalParts) {
    // All parts done, just need to complete
    return await completeUpload(uploadId);
  }

  // 2. Get fresh presigned URLs for remaining parts
  const remaining = await fetch(`/api/uploads/${uploadId}/remaining-parts`);
  const { presignedUrls } = await remaining.json();

  // 3. Upload only the missing parts
  const results = await uploadParts(file, presignedUrls, chunkSize);

  // 4. Complete
  return await completeUpload(uploadId);
}
```

---

## Checksum & File Integrity Verification (Deep Dive)

### The Problem

When you split a 50GB file into 512 chunks, upload them over the internet, and S3 reassembles them — how do you know the final object is bit-for-bit identical to the original? Corruption can happen at multiple points:
- Network bit flips during transfer
- Incomplete/truncated chunk writes
- Client-side file read errors
- Parts assembled in wrong order (unlikely with S3, but defense in depth)

### Integrity Verification Flow Diagram

```
                        UPLOAD TIME (per-part checksums)
                        ================================

Client                                              S3
  |                                                  |
  |  file.slice(0, 100MB) → sha256 = "aaa111"        |
  |-- PUT part 1 + header Content-MD5: <base64> ---->|
  |                                     S3 validates |
  |                                     MD5 matches  |
  |<-- 200 OK + ETag: "aaa111" ----------------------|
  |                                                  |
  |  file.slice(100MB, 200MB) → sha256 = "bbb222"    |
  |-- PUT part 2 + header Content-MD5: <base64> ---->|
  |                                     S3 validates |
  |<-- 200 OK + ETag: "bbb222" ----------------------|
  |                                                  |
  |  ... repeat for all 512 parts ...                |


                        COMPLETION TIME (whole-file checksum)
                        =====================================

Client              Upload Service                    S3                    Database
  |                        |                            |                       |
  |                        |-- CompleteMultipartUpload->|                       |
  |                        |<-- 200 OK, assembled ------|                       |
  |                        |                            |                       |
  |                        |-- HeadObject or            |                       |
  |                        |   GetObjectAttributes ---->|                       |
  |                        |<-- S3 checksum/ETag -------|                       |
  |                        |                            |                       |
  |                        |-- Compare:                 |                       |
  |                        |   stored_checksum (DB)     |                       |
  |                        |     vs                     |                       |
  |                        |   actual_checksum (S3)     |                       |
  |                        |                            |                       |
  |                        |   ✅ MATCH → mark complete|                       |
  |                        |   ❌ MISMATCH → mark      |                       |
  |                        |      corrupted, alert     |                       |
  |                        |-- UPDATE status --------->|-----------------------|
  |<-- result -------------|                           |                       |


                        DEDUPLICATION CHECK (before upload starts)
                        ==========================================

Client              Upload Service                              Database
  |                        |                                       |
  |-- POST /initiate       |                                       |
  |   checksum: "xyz789" ->|                                       |
  |                        |-- SELECT * FROM file_uploads          |
  |                        |   WHERE checksum = 'xyz789'           |
  |                        |   AND status = 'completed' ---------->|
  |                        |                                       |
  |                        |<-- Found existing upload! ------------|
  |                        |                                       |
  |<-- 200 OK              |                                       |
  |   "File already exists"|                                       |
  |   s3Url: "s3://..."    |                                       |
  |   (skip entire upload) |                                       |
```

### Three Layers of Integrity

| Layer | What | How | When |
|-------|------|-----|------|
| Per-part | Each chunk is intact | Client sends `Content-MD5` header; S3 rejects if mismatch | During upload |
| Whole-file (S3) | S3 reassembled correctly | Use `x-amz-checksum-sha256` with `CompleteMultipartUpload` | At completion |
| Application-level | End-to-end match | Compare client-provided checksum vs post-upload checksum | After completion |

### Implementation

**Per-part checksum (client-side):**
```javascript
async function uploadPartWithChecksum(url, blob, partNumber) {
  // Calculate MD5 for Content-MD5 header (S3 requirement: base64-encoded)
  const arrayBuffer = await blob.arrayBuffer();
  const hashBuffer = await crypto.subtle.digest('MD5', arrayBuffer);
  const contentMD5 = btoa(String.fromCharCode(...new Uint8Array(hashBuffer)));

  // Calculate SHA-256 for application-level tracking
  const sha256Buffer = await crypto.subtle.digest('SHA-256', arrayBuffer);
  const sha256Hex = [...new Uint8Array(sha256Buffer)]
    .map(b => b.toString(16).padStart(2, '0')).join('');

  const response = await fetch(url, {
    method: 'PUT',
    body: blob,
    headers: {
      'Content-MD5': contentMD5,
      'x-amz-checksum-sha256': sha256Hex
    }
  });

  return {
    partNumber,
    etag: response.headers.get('ETag'),
    checksum: sha256Hex
  };
}
```

**Whole-file verification (server-side):**
```python
def verify_upload_integrity(upload_id):
    upload = db.get_upload(upload_id)

    # Get the checksum from S3 after completion
    response = s3.head_object(
        Bucket=upload['s3_bucket'],
        Key=upload['s3_key'],
        ChecksumMode='ENABLED'
    )

    s3_checksum = response.get('ChecksumSHA256')
    stored_checksum = upload['checksum']  # from initial request

    if s3_checksum == stored_checksum:
        db.update_status(upload_id, 'verified')
        return True
    else:
        db.update_status(upload_id, 'corrupted')
        notify_admin(upload_id, 'Checksum mismatch detected')
        return False
```

### What Happens on Mismatch

1. Mark the upload as `corrupted` in the database
2. Do NOT delete the S3 object (keep for investigation)
3. Notify the client that integrity verification failed
4. Client can re-initiate a fresh upload
5. Optionally: identify which specific part had the bad checksum by comparing per-part checksums stored in `upload_parts`
