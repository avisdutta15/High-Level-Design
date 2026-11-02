# Kubernetes Complete Tutorial — Full Depth with Examples

---

## 1. What is Kubernetes?

Kubernetes (K8s) is an open-source container orchestration platform that automates deployment, scaling, and management of containerized applications. Originally developed by Google (based on their internal system Borg), now maintained by the Cloud Native Computing Foundation (CNCF).

### What problems does Kubernetes solve?

| Problem | Without K8s | With K8s |
|---|---|---|
| App goes down | Manual restart, pager alerts at 3 AM | Auto-heals, restarts crashed containers |
| Traffic spikes | Manually spin up more servers | Auto-scales pods based on CPU/memory/custom metrics |
| Deployments | Downtime during updates | Rolling updates with zero downtime |
| Service discovery | Hardcoded IPs, manual load balancer config | Built-in DNS and service discovery |
| Config management | Config files baked into images | ConfigMaps and Secrets, decoupled from images |
| Resource efficiency | One app per VM, wasted resources | Bin-packs containers onto nodes efficiently |
| Multi-environment | Different scripts for dev/staging/prod | Same manifests, different configs per namespace |

---

## 2. Kubernetes Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        CONTROL PLANE                            │
│                                                                 │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────────────┐ │
│  │  API Server   │  │  Scheduler   │  │  Controller Manager   │ │
│  │ (kube-apiserver)│ │              │  │  - Node Controller    │ │
│  │              │  │ Decides which │  │  - ReplicaSet Ctrl    │ │
│  │ Gateway to   │  │ node runs a  │  │  - Deployment Ctrl    │ │
│  │ everything   │  │ new pod      │  │  - Job Controller     │ │
│  └──────┬───────┘  └──────────────┘  └───────────────────────┘ │
│         │                                                       │
│  ┌──────▼───────┐  ┌──────────────────────────────────────────┐│
│  │    etcd       │  │  Cloud Controller Manager (optional)     ││
│  │  Key-value    │  │  Integrates with AWS/GCP/Azure           ││
│  │  store        │  │  (load balancers, storage, nodes)        ││
│  └──────────────┘  └──────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
                              │
                    ┌─────────▼─────────┐
                    │     Network        │
                    └─────────┬─────────┘
                              │
┌─────────────────────────────▼───────────────────────────────────┐
│                        WORKER NODES                             │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Node 1                                                  │   │
│  │  ┌──────────┐  ┌──────────────┐  ┌──────────────────┐  │   │
│  │  │  kubelet  │  │  kube-proxy  │  │ Container Runtime│  │   │
│  │  │          │  │              │  │ (containerd)     │  │   │
│  │  └──────────┘  └──────────────┘  └──────────────────┘  │   │
│  │                                                         │   │
│  │  ┌─────────┐  ┌─────────┐  ┌─────────┐                │   │
│  │  │  Pod A  │  │  Pod B  │  │  Pod C  │                │   │
│  │  └─────────┘  └─────────┘  └─────────┘                │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Node 2                                                  │   │
│  │  ┌──────────┐  ┌──────────────┐  ┌──────────────────┐  │   │
│  │  │  kubelet  │  │  kube-proxy  │  │ Container Runtime│  │   │
│  │  └──────────┘  └──────────────┘  └──────────────────┘  │   │
│  │                                                         │   │
│  │  ┌─────────┐  ┌─────────┐                              │   │
│  │  │  Pod D  │  │  Pod E  │                              │   │
│  │  └─────────┘  └─────────┘                              │   │
│  └─────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

### Control Plane Components

| Component | Role |
|---|---|
| **kube-apiserver** | Front door to the cluster. All communication goes through it (CLI, UI, internal components). RESTful API. |
| **etcd** | Distributed key-value store. The single source of truth for cluster state. |
| **kube-scheduler** | Watches for new pods with no assigned node, selects a node based on resource requirements, affinity rules, taints/tolerations. |
| **kube-controller-manager** | Runs controller loops that watch cluster state and make changes to move current state toward desired state. |
| **cloud-controller-manager** | Integrates with cloud provider APIs (AWS ELB, EBS, etc.) |

### Worker Node Components

| Component | Role |
|---|---|
| **kubelet** | Agent on each node. Ensures containers described in PodSpecs are running and healthy. |
| **kube-proxy** | Network proxy on each node. Maintains network rules for pod communication (iptables/IPVS). |
| **Container Runtime** | Runs containers (containerd, CRI-O). Docker was deprecated as runtime in K8s 1.24+. |

---

## 3. Core Concepts and Objects

### The Pod — Smallest Deployable Unit

A pod is one or more containers that share network namespace (same IP), storage volumes, and lifecycle.

```yaml
# pod.yaml — a simple pod
apiVersion: v1
kind: Pod
metadata:
  name: my-app
  labels:
    app: web
    environment: dev
spec:
  containers:
    - name: web
      image: nginx:1.25
      ports:
        - containerPort: 80
      resources:
        requests:
          memory: "64Mi"
          cpu: "250m"
        limits:
          memory: "128Mi"
          cpu: "500m"
```

```bash
kubectl apply -f pod.yaml
kubectl get pods
kubectl describe pod my-app
kubectl logs my-app
kubectl exec -it my-app -- bash
kubectl delete pod my-app
```

### Multi-Container Pod Patterns

```yaml
# Sidecar pattern — log collector alongside main app
apiVersion: v1
kind: Pod
metadata:
  name: app-with-sidecar
spec:
  containers:
    - name: app
      image: myapp:1.0
      volumeMounts:
        - name: shared-logs
          mountPath: /var/log/app

    - name: log-collector
      image: fluentd:latest
      volumeMounts:
        - name: shared-logs
          mountPath: /var/log/app

  volumes:
    - name: shared-logs
      emptyDir: {}
```

**Pod patterns:**
- **Sidecar** — helper container (log shipping, proxy, config sync)
- **Ambassador** — proxy that simplifies network communication
- **Init Container** — runs to completion before main containers start

```yaml
# Init container example — wait for database before starting app
spec:
  initContainers:
    - name: wait-for-db
      image: busybox
      command: ['sh', '-c', 'until nc -z db-service 5432; do echo waiting for db; sleep 2; done']
  containers:
    - name: app
      image: myapp:1.0
```

---

## 4. Workload Resources

### ReplicaSet

Ensures a specified number of pod replicas are running at all times. Rarely used directly — Deployments manage ReplicaSets.

```yaml
apiVersion: apps/v1
kind: ReplicaSet
metadata:
  name: web-rs
spec:
  replicas: 3
  selector:
    matchLabels:
      app: web
  template:
    metadata:
      labels:
        app: web
    spec:
      containers:
        - name: web
          image: nginx:1.25
```

### Deployment — The Standard Workload

Manages ReplicaSets, provides declarative updates, rollback, and scaling.

```yaml
# deployment.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: web-app
  labels:
    app: web
spec:
  replicas: 3
  selector:
    matchLabels:
      app: web
  strategy:
    type: RollingUpdate
    rollingUpdate:
      maxSurge: 1           # Max pods above desired count during update
      maxUnavailable: 0     # Zero downtime — always keep all replicas up
  template:
    metadata:
      labels:
        app: web
        version: "2.0"
    spec:
      containers:
        - name: web
          image: myapp:2.0
          ports:
            - containerPort: 8080
          resources:
            requests:
              cpu: "100m"
              memory: "128Mi"
            limits:
              cpu: "500m"
              memory: "256Mi"
          readinessProbe:
            httpGet:
              path: /health
              port: 8080
            initialDelaySeconds: 5
            periodSeconds: 10
          livenessProbe:
            httpGet:
              path: /health
              port: 8080
            initialDelaySeconds: 15
            periodSeconds: 20
```

```bash
# Apply the deployment
kubectl apply -f deployment.yaml

# Check rollout status
kubectl rollout status deployment/web-app

# Scale
kubectl scale deployment/web-app --replicas=5

# Update image (triggers rolling update)
kubectl set image deployment/web-app web=myapp:2.1

# View rollout history
kubectl rollout history deployment/web-app

# Rollback to previous version
kubectl rollout undo deployment/web-app

# Rollback to specific revision
kubectl rollout undo deployment/web-app --to-revision=2
```

### How ReplicaSets and Deployments Match Pods

ReplicaSets and Deployments match pods using label selectors. It's a loose coupling mechanism — there's no parent-child reference, just labels.

**1. You define labels in the pod template:**
```yaml
spec:
  template:
    metadata:
      labels:
        app: web
        version: "2.0"
```

**2. You define a selector that matches those labels:**
```yaml
spec:
  selector:
    matchLabels:
      app: web
```

The ReplicaSet continuously queries the API server: "how many pods exist with label `app=web`?" If there are fewer than `replicas`, it creates more. If there are more, it deletes some.

**Key details:**

- The selector must be a subset of the pod template labels. The pod can have extra labels (`version: "2.0"`), but it must at least have the ones in the selector.
- The selector is immutable after creation — you can't change it.
- Any pod with matching labels counts, even if you created it manually with `kubectl run`. The ReplicaSet will adopt it (or kill it if it's over the replica count).
- Deployments don't directly own pods. A Deployment creates a ReplicaSet, and the ReplicaSet creates the pods. The chain is: `Deployment → ReplicaSet → Pods`.

**How ownership actually works under the hood:**

Each pod gets an `ownerReferences` field pointing back to its ReplicaSet:

```yaml
metadata:
  ownerReferences:
    - apiVersion: apps/v1
      kind: ReplicaSet
      name: web-app-6d4f5b7c8
      uid: abc-123
      controller: true
```

This is how Kubernetes knows which ReplicaSet "owns" which pods — labels find them, ownerReferences track ownership. If you delete the ownerReference from a pod, the ReplicaSet will create a new one (it now sees one fewer pod matching the selector) and the orphaned pod keeps running independently.

**When a Deployment updates:**

```
Deployment: web-app
  ├── ReplicaSet: web-app-6d4f5b7c8  (old, image: v1, replicas: 0)
  │     └── (no pods)
  └── ReplicaSet: web-app-8a9b1c2d3  (new, image: v2, replicas: 3)
        ├── Pod: web-app-8a9b1c2d3-x7k2j
        ├── Pod: web-app-8a9b1c2d3-m3n4p
        └── Pod: web-app-8a9b1c2d3-q9r8s
```

The Deployment creates a new ReplicaSet with the updated pod template. The new RS scales up while the old one scales down (rolling update). The old ReplicaSet sticks around with 0 replicas — that's how `kubectl rollout undo` works instantly, it just scales the old RS back up.

### Where Do Labels Go — spec.template vs metadata?

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: web-app
  labels:
    app: web          # ← Labels on the Deployment ITSELF
spec:
  selector:
    matchLabels:
      app: web        # ← What the ReplicaSet uses to find pods
  template:
    metadata:
      labels:
        app: web      # ← Labels on the PODS that get created
    spec:
      containers:
        - name: web
          image: nginx
```

- `metadata.labels` — labels on the Deployment object itself. Used if you want to query or organize Deployments (e.g., `kubectl get deployments -l app=web`). Has no effect on pod matching.
- `spec.template.metadata.labels` — labels on the pods that the ReplicaSet creates. This is what the `selector.matchLabels` actually matches against.

The `selector.matchLabels` must match `spec.template.metadata.labels`. If they don't match, Kubernetes rejects the manifest with a validation error. The top-level `metadata.labels` on the Deployment is completely independent — it can be different, or even absent.

### Deployment Strategies

| Strategy | How it works | Use case |
|---|---|---|
| **RollingUpdate** | Gradually replaces old pods with new | Default. Zero-downtime updates |
| **Recreate** | Kills all old pods, then creates new | When you can't run two versions simultaneously |

For advanced strategies (blue/green, canary), use Argo Rollouts or Istio.

### StatefulSet — For Stateful Applications

Provides stable network identities, persistent storage, and ordered deployment/scaling.

```yaml
apiVersion: apps/v1
kind: StatefulSet
metadata:
  name: postgres
spec:
  serviceName: "postgres"     # Headless service for stable DNS
  replicas: 3
  selector:
    matchLabels:
      app: postgres
  template:
    metadata:
      labels:
        app: postgres
    spec:
      containers:
        - name: postgres
          image: postgres:15
          ports:
            - containerPort: 5432
          volumeMounts:
            - name: data
              mountPath: /var/lib/postgresql/data
          env:
            - name: POSTGRES_PASSWORD
              valueFrom:
                secretKeyRef:
                  name: postgres-secret
                  key: password
  volumeClaimTemplates:       # Each pod gets its own PVC
    - metadata:
        name: data
      spec:
        accessModes: ["ReadWriteOnce"]
        storageClassName: gp3
        resources:
          requests:
            storage: 10Gi
```

**StatefulSet guarantees:**
- Pod names are predictable: `postgres-0`, `postgres-1`, `postgres-2`
- DNS entries: `postgres-0.postgres.default.svc.cluster.local`
- Pods are created in order (0 → 1 → 2) and deleted in reverse
- Each pod gets its own PersistentVolumeClaim

### DaemonSet — One Pod Per Node

Ensures a copy of a pod runs on every node (or selected nodes). Used for cluster-wide agents.

```yaml
apiVersion: apps/v1
kind: DaemonSet
metadata:
  name: log-agent
spec:
  selector:
    matchLabels:
      app: log-agent
  template:
    metadata:
      labels:
        app: log-agent
    spec:
      containers:
        - name: fluentd
          image: fluentd:latest
          volumeMounts:
            - name: varlog
              mountPath: /var/log
      volumes:
        - name: varlog
          hostPath:
            path: /var/log
```

Use cases: log collectors, monitoring agents, network plugins, storage drivers.

### Job and CronJob — Batch Workloads

**Job** — runs a task once to completion, then stops. Think: database migration, data export, batch processing.

```yaml
# Job — runs to completion
apiVersion: batch/v1
kind: Job
metadata:
  name: data-migration
spec:
  completions: 3              # Run 3 pods total to completion
  parallelism: 2              # Run 2 at a time
  backoffLimit: 4             # Retry up to 4 times on failure
  activeDeadlineSeconds: 600  # Kill it if not done in 10 minutes
  template:
    spec:
      containers:
        - name: migrate
          image: myapp:1.0
          command: ["python", "migrate.py"]
      restartPolicy: Never
```

**CronJob** — creates a Job on a schedule (cron syntax). Think: nightly backups, hourly reports, daily cleanup. It's just a Job factory that runs on a timer.

```yaml
# CronJob — scheduled job
apiVersion: batch/v1
kind: CronJob
metadata:
  name: nightly-backup
spec:
  schedule: "0 2 * * *"              # Every day at 2 AM
  concurrencyPolicy: Forbid           # Don't start new if previous still running
  startingDeadlineSeconds: 200        # Skip if it can't start within 200s of scheduled time
  successfulJobsHistoryLimit: 3       # Keep last 3 successful Job objects
  failedJobsHistoryLimit: 1           # Keep last 1 failed Job object
  jobTemplate:
    spec:
      template:
        spec:
          containers:
            - name: backup
              image: backup-tool:1.0
              command: ["./backup.sh"]
          restartPolicy: OnFailure
```

### Job vs CronJob — Key Differences

| | Job | CronJob |
|---|---|---|
| Trigger | Manual / one-time | Scheduled (cron expression) |
| Creates | Pods directly | Creates a Job, which creates pods |
| Lifecycle | Runs once, done | Keeps creating Jobs on schedule |
| Use case | Migrations, one-off tasks | Backups, reports, cleanup |

The relationship is: `CronJob → creates Job → creates Pod`. A CronJob is to a Job what a Deployment is to a ReplicaSet — a higher-level controller that manages the lower one.

**concurrencyPolicy options:**
- `Allow` — multiple Jobs can run at the same time (default)
- `Forbid` — skip the new run if the previous one is still going
- `Replace` — kill the running Job and start a new one

---

## 5. Services and Networking

### Service Types

```
                    Internet
                       │
              ┌────────▼────────┐
              │   LoadBalancer   │  ← Cloud LB (AWS ALB/NLB)
              │   Service        │
              └────────┬────────┘
                       │
              ┌────────▼────────┐
              │    NodePort      │  ← Opens port on every node
              │    Service       │
              └────────┬────────┘
                       │
              ┌────────▼────────┐
              │    ClusterIP     │  ← Internal only (default)
              │    Service       │
              └────────┬────────┘
                       │
              ┌────────▼────────┐
              │    Pods          │
              └─────────────────┘
```

### ClusterIP — Internal Service

```yaml
apiVersion: v1
kind: Service
metadata:
  name: api-service
spec:
  type: ClusterIP            # Default
  selector:
    app: api
  ports:
    - port: 80               # Service port
      targetPort: 8080       # Container port
      protocol: TCP
```

Other pods reach this service at `api-service.default.svc.cluster.local:80` or just `api-service:80` within the same namespace.

### NodePort — External Access via Node IP

```yaml
apiVersion: v1
kind: Service
metadata:
  name: web-nodeport
spec:
  type: NodePort
  selector:
    app: web
  ports:
    - port: 80
      targetPort: 8080
      nodePort: 30080        # Accessible at <NodeIP>:30080
```

### LoadBalancer — Cloud Provider Load Balancer

```yaml
apiVersion: v1
kind: Service
metadata:
  name: web-lb
  annotations:
    service.beta.kubernetes.io/aws-load-balancer-type: "nlb"
spec:
  type: LoadBalancer
  selector:
    app: web
  ports:
    - port: 80
      targetPort: 8080
```

### Headless Service — Direct Pod DNS

A normal Service gets a virtual IP (ClusterIP). When you call it, kube-proxy load-balances across the pods behind it. You never know which pod you're talking to.

A headless service has `clusterIP: None`. No virtual IP, no load balancing. Instead, a DNS lookup returns the IP addresses of every individual pod.

```yaml
apiVersion: v1
kind: Service
metadata:
  name: postgres
spec:
  clusterIP: None             # This makes it headless — no virtual IP
  selector:
    app: postgres
  ports:
    - port: 5432
```

**Normal service DNS:**
```
nslookup api-service
→ 10.96.45.12              # Single virtual IP (load balanced)
```

**Headless service DNS:**
```
nslookup postgres
→ 10.244.1.5               # Pod postgres-0
→ 10.244.2.8               # Pod postgres-1
→ 10.244.3.3               # Pod postgres-2
```

Each pod also gets a stable, predictable DNS name (used with StatefulSets):
```
postgres-0.postgres.default.svc.cluster.local
postgres-1.postgres.default.svc.cluster.local
postgres-2.postgres.default.svc.cluster.local
```

**Why is this useful?**

1. **StatefulSets need it.** Databases need to know exactly which pod is the primary to replicate from. You can't do that with a load-balanced IP that sends you to a random pod.
2. **Client-side load balancing.** The client gets all pod IPs and decides which one to call. Useful when your app has its own routing logic (e.g., consistent hashing, gRPC connection balancing).
3. **Service discovery without proxying.** Some apps (Kafka, Elasticsearch, Cassandra) need to know peer addresses for cluster formation. Headless services give them that.

**Normal Service vs Headless Service:**

| | Normal Service (ClusterIP) | Headless Service |
|---|---|---|
| Has virtual IP | Yes | No |
| Load balancing | kube-proxy handles it | Client's responsibility |
| DNS returns | Single ClusterIP | All pod IPs |
| Pod-specific DNS | No | Yes (`pod-name.service-name`) |
| Use case | Stateless apps (web, API) | Databases, message queues, peer-to-peer discovery |

### ExternalName — DNS Alias

```yaml
apiVersion: v1
kind: Service
metadata:
  name: external-db
spec:
  type: ExternalName
  externalName: prod-db.abc123.us-east-1.rds.amazonaws.com
```

Pods can use `external-db` as the hostname, which resolves to the external DNS name.

---

## 6. Ingress — HTTP/HTTPS Routing

Ingress provides HTTP routing, SSL termination, and name-based virtual hosting. Requires an Ingress Controller (NGINX, Traefik, AWS ALB Ingress Controller).

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: app-ingress
  annotations:
    nginx.ingress.kubernetes.io/rewrite-target: /
    cert-manager.io/cluster-issuer: letsencrypt-prod
spec:
  ingressClassName: nginx
  tls:
    - hosts:
        - app.example.com
      secretName: app-tls-secret
  rules:
    - host: app.example.com
      http:
        paths:
          - path: /api
            pathType: Prefix
            backend:
              service:
                name: api-service
                port:
                  number: 80
          - path: /
            pathType: Prefix
            backend:
              service:
                name: frontend-service
                port:
                  number: 80
```

### AWS ALB Ingress

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: app-ingress
  annotations:
    kubernetes.io/ingress.class: alb
    alb.ingress.kubernetes.io/scheme: internet-facing
    alb.ingress.kubernetes.io/target-type: ip
    alb.ingress.kubernetes.io/certificate-arn: arn:aws:acm:us-east-1:123456789:certificate/xxx
spec:
  rules:
    - host: app.example.com
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: web-service
                port:
                  number: 80
```

### Network Policies — Pod-Level Firewall

```yaml
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: api-network-policy
  namespace: production
spec:
  podSelector:
    matchLabels:
      app: api
  policyTypes:
    - Ingress
    - Egress
  ingress:
    - from:
        - podSelector:
            matchLabels:
              app: frontend
        - namespaceSelector:
            matchLabels:
              env: production
      ports:
        - protocol: TCP
          port: 8080
  egress:
    - to:
        - podSelector:
            matchLabels:
              app: database
      ports:
        - protocol: TCP
          port: 5432
    - to:                         # Allow DNS
        - namespaceSelector: {}
      ports:
        - protocol: UDP
          port: 53
```

---

## 7. Configuration Management

### ConfigMap

```yaml
# configmap.yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: app-config
data:
  DATABASE_HOST: "postgres-service"
  DATABASE_PORT: "5432"
  LOG_LEVEL: "info"
  app.properties: |
    server.port=8080
    feature.flag.new-ui=true
    cache.ttl=300
```

Using ConfigMaps in pods:

```yaml
spec:
  containers:
    - name: app
      image: myapp:1.0

      # Method 1: Environment variables from specific keys
      env:
        - name: DB_HOST
          valueFrom:
            configMapKeyRef:
              name: app-config
              key: DATABASE_HOST

      # Method 2: All keys as environment variables
      envFrom:
        - configMapRef:
            name: app-config

      # Method 3: Mount as files
      volumeMounts:
        - name: config-volume
          mountPath: /etc/config

  volumes:
    - name: config-volume
      configMap:
        name: app-config
```

### Secrets

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: db-secret
type: Opaque
data:
  username: YWRtaW4=           # base64 encoded "admin"
  password: cEBzc3dvcmQxMjM=  # base64 encoded "p@ssword123"

# Or use stringData (plain text, encoded automatically)
---
apiVersion: v1
kind: Secret
metadata:
  name: db-secret
type: Opaque
stringData:
  username: admin
  password: p@ssword123
```

```bash
# Create secret from command line
kubectl create secret generic db-secret \
  --from-literal=username=admin \
  --from-literal=password='p@ssword123'

# Create secret from file
kubectl create secret generic tls-cert \
  --from-file=cert.pem \
  --from-file=key.pem
```

Using secrets in pods:

```yaml
spec:
  containers:
    - name: app
      env:
        - name: DB_PASSWORD
          valueFrom:
            secretKeyRef:
              name: db-secret
              key: password
      volumeMounts:
        - name: secret-volume
          mountPath: /etc/secrets
          readOnly: true
  volumes:
    - name: secret-volume
      secret:
        secretName: db-secret
```

Important: Kubernetes Secrets are base64-encoded, not encrypted. For real encryption use:
- AWS Secrets Manager + External Secrets Operator
- HashiCorp Vault
- Sealed Secrets
- KMS envelope encryption for etcd

---

## 8. Storage

Containers are ephemeral — when a container restarts, its filesystem is wiped. Kubernetes storage solves this with volumes that outlive containers and even pods.

### Volume Types — Quick Overview

| Type | Lifetime | Use Case |
|---|---|---|
| `emptyDir` | Same as pod (deleted when pod dies) | Scratch space, shared temp data between containers |
| `hostPath` | Tied to a specific node | Access node-level files (logs, Docker socket) |
| `configMap` / `secret` | Tied to the K8s object | Inject config files or secrets as volumes |
| `PersistentVolumeClaim` | Independent of pod lifecycle | Databases, stateful apps, anything that must survive restarts |

### emptyDir — Temporary Shared Storage

Created when a pod starts, deleted when the pod is removed. Useful for sharing data between containers in the same pod.

```yaml
apiVersion: v1
kind: Pod
metadata:
  name: shared-data
spec:
  containers:
    - name: writer
      image: busybox
      command: ["sh", "-c", "echo hello > /data/message.txt && sleep 3600"]
      volumeMounts:
        - name: shared              # Mount the volume
          mountPath: /data           # Writer writes here

    - name: reader
      image: busybox
      command: ["sh", "-c", "cat /data/message.txt && sleep 3600"]
      volumeMounts:
        - name: shared              # Same volume name
          mountPath: /data           # Reader reads from here

  volumes:
    - name: shared
      emptyDir: {}                   # Created empty, lives as long as the pod
      # emptyDir:
      #   medium: Memory             # Use RAM-backed tmpfs (faster but uses pod memory limit)
      #   sizeLimit: 100Mi           # Cap the size
```

### hostPath — Access Node Filesystem

Mounts a file or directory from the host node's filesystem. The data persists beyond the pod but is tied to that specific node.

```yaml
volumes:
  - name: docker-sock
    hostPath:
      path: /var/run/docker.sock     # Path on the host
      type: Socket                   # Validates it's a socket
```

**hostPath types:** `DirectoryOrCreate`, `Directory`, `FileOrCreate`, `File`, `Socket`, `CharDevice`, `BlockDevice`

Warning: hostPath is a security risk (pod can access host files) and not portable (pod must run on the same node). Avoid in production — use PersistentVolumes instead.

### PersistentVolume (PV) and PersistentVolumeClaim (PVC)

PV/PVC decouples storage provisioning from storage consumption. Admins manage storage (PV), developers request it (PVC).

```
┌──────────────┐     ┌──────────────┐     ┌──────────────────┐
│     Pod      │────▶│     PVC      │────▶│       PV         │
│  (Consumer)  │     │  (Request)   │     │  (Actual Storage) │
│              │     │  "I need     │     │  "I am 10Gi of   │
│              │     │   5Gi SSD"   │     │   EBS gp3"       │
└──────────────┘     └──────────────┘     └──────────────────┘
                                                   │
                                          ┌────────▼────────┐
                                          │  Storage Backend │
                                          │  (EBS, Azure Disk│
                                          │   NFS, EFS, etc) │
                                          └─────────────────┘
```

**PV lifecycle:**

```
Available → Bound → Released → (Retained / Deleted / Recycled)
    │          │        │
    │          │        └─ PVC deleted, PV still has data
    │          └─ PVC is bound to this PV
    └─ PV exists, no PVC bound to it
```

### Static Provisioning

Admin pre-creates PVs, developers claim them with PVCs. Kubernetes matches PVCs to PVs by access mode, storage size, and storageClassName.

```yaml
# Step 1: Admin creates PV (the actual storage)
apiVersion: v1
kind: PersistentVolume
metadata:
  name: my-pv
  labels:
    type: local
spec:
  capacity:
    storage: 10Gi                          # Total capacity of this volume
  accessModes:
    - ReadWriteOnce                        # Can be mounted read-write by one node
  persistentVolumeReclaimPolicy: Retain    # What happens when PVC is deleted (see table below)
  storageClassName: manual                 # Must match PVC's storageClassName
  hostPath:                                # Storage backend (hostPath for local, awsElasticBlockStore for EBS, etc.)
    path: /mnt/data

---
# Step 2: Developer creates PVC (a request for storage)
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: my-pvc
spec:
  accessModes:
    - ReadWriteOnce                        # Must match PV's access mode
  storageClassName: manual                 # Must match PV's storageClassName
  resources:
    requests:
      storage: 5Gi                         # Requesting 5Gi (will bind to a PV >= 5Gi)

---
# Step 3: Pod uses the PVC
apiVersion: v1
kind: Pod
metadata:
  name: app
spec:
  containers:
    - name: app
      image: myapp:1.0
      volumeMounts:
        - name: data
          mountPath: /app/data             # Where it appears inside the container
  volumes:
    - name: data
      persistentVolumeClaim:
        claimName: my-pvc                  # Reference the PVC by name
```

```bash
# Check PV and PVC status
kubectl get pv
# NAME    CAPACITY   ACCESS MODES   RECLAIM POLICY   STATUS   CLAIM            STORAGECLASS
# my-pv   10Gi       RWO            Retain           Bound    default/my-pvc   manual

kubectl get pvc
# NAME     STATUS   VOLUME   CAPACITY   ACCESS MODES   STORAGECLASS
# my-pvc   Bound    my-pv    10Gi       RWO            manual
```

### Dynamic Provisioning (preferred)

No need to pre-create PVs. Define a StorageClass and Kubernetes automatically provisions storage when a PVC is created.

```yaml
# Step 1: StorageClass — defines HOW to provision storage
apiVersion: storage.k8s.io/v1
kind: StorageClass
metadata:
  name: fast-ssd
  annotations:
    storageclass.kubernetes.io/is-default-class: "true"   # Make this the default StorageClass
provisioner: ebs.csi.aws.com          # CSI driver (cloud-specific)
parameters:                            # Provisioner-specific settings
  type: gp3                            # EBS volume type
  iops: "3000"                         # Provisioned IOPS
  throughput: "125"                    # MB/s throughput
  encrypted: "true"                    # Encrypt at rest
reclaimPolicy: Delete                  # Delete EBS volume when PVC is deleted
volumeBindingMode: WaitForFirstConsumer  # Don't provision until a pod needs it (avoids zone mismatch)
allowVolumeExpansion: true             # Allow PVC resize after creation

---
# Step 2: PVC — storage is automatically provisioned by the StorageClass
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: app-data
spec:
  accessModes:
    - ReadWriteOnce
  storageClassName: fast-ssd           # References the StorageClass (omit to use default)
  resources:
    requests:
      storage: 20Gi                    # A 20Gi EBS gp3 volume is automatically created
```

```bash
# List storage classes
kubectl get storageclass
# NAME                 PROVISIONER          RECLAIMPOLICY   VOLUMEBINDINGMODE       ALLOWVOLUMEEXPANSION
# fast-ssd (default)   ebs.csi.aws.com      Delete          WaitForFirstConsumer    true
# gp2                  kubernetes.io/aws-ebs Delete          Immediate               false
```

### Expanding a PVC (Resizing)

If `allowVolumeExpansion: true` on the StorageClass:

```bash
# Edit the PVC to request more storage
kubectl patch pvc app-data -p '{"spec":{"resources":{"requests":{"storage":"50Gi"}}}}'

# Check status — may need pod restart for filesystem expansion
kubectl get pvc app-data
kubectl describe pvc app-data    # Look for "FileSystemResizePending" condition
```

Note: You can only grow PVCs, never shrink them.

### Access Modes

| Mode | Abbreviation | Description | Supported By |
|---|---|---|---|
| ReadWriteOnce | RWO | Mounted read-write by a single node | EBS, Azure Disk, GCE PD |
| ReadOnlyMany | ROX | Mounted read-only by many nodes | NFS, EFS, Azure Files |
| ReadWriteMany | RWX | Mounted read-write by many nodes | NFS, EFS, Azure Files, CephFS |
| ReadWriteOncePod | RWOP | Mounted read-write by a single pod (K8s 1.27+) | CSI drivers that support it |

Important: RWO means one node, not one pod. Multiple pods on the same node can all mount a RWO volume.

### Reclaim Policies

What happens to the PV and its data when the PVC is deleted:

| Policy | Behavior | Use Case |
|---|---|---|
| `Retain` | PV and data are kept. PV enters "Released" state. Admin must manually clean up. | Production data you can't afford to lose |
| `Delete` | PV and underlying storage (EBS volume, Azure Disk) are deleted. | Dev/test, ephemeral workloads |
| `Recycle` | Data is deleted (`rm -rf /volume/*`), PV becomes Available again. Deprecated — use dynamic provisioning instead. | Legacy |

```bash
# Change reclaim policy on existing PV
kubectl patch pv my-pv -p '{"spec":{"persistentVolumeReclaimPolicy":"Retain"}}'
```

### volumeBindingMode

| Mode | Behavior | Use Case |
|---|---|---|
| `Immediate` | PV is provisioned as soon as PVC is created | When you know the zone doesn't matter |
| `WaitForFirstConsumer` | PV is provisioned only when a pod using the PVC is scheduled | Prevents zone mismatch — volume is created in same zone as the pod |

`WaitForFirstConsumer` is recommended. Without it, a volume might be created in `eu-west-1a` but the pod gets scheduled in `eu-west-1b` — and cross-zone volume mounts don't work for block storage.

### Common Storage Backends by Cloud

| Cloud | Block (RWO) | File (RWX) | Provisioner |
|---|---|---|---|
| AWS | EBS (gp3, io2) | EFS | `ebs.csi.aws.com`, `efs.csi.aws.com` |
| Azure | Azure Disk | Azure Files | `disk.csi.azure.com`, `file.csi.azure.com` |
| GCP | Persistent Disk | Filestore | `pd.csi.storage.gke.io`, `filestore.csi.storage.gke.io` |
| On-prem | Local, iSCSI | NFS, CephFS | Various CSI drivers |

### Practical Example — Database with Persistent Storage

```yaml
apiVersion: storage.k8s.io/v1
kind: StorageClass
metadata:
  name: db-storage
provisioner: ebs.csi.aws.com
parameters:
  type: io2
  iops: "5000"
reclaimPolicy: Retain              # Never auto-delete database volumes
volumeBindingMode: WaitForFirstConsumer
allowVolumeExpansion: true

---
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: postgres-data
spec:
  accessModes: ["ReadWriteOnce"]
  storageClassName: db-storage
  resources:
    requests:
      storage: 100Gi

---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: postgres
spec:
  replicas: 1
  selector:
    matchLabels:
      app: postgres
  template:
    metadata:
      labels:
        app: postgres
    spec:
      containers:
        - name: postgres
          image: postgres:15
          volumeMounts:
            - name: data
              mountPath: /var/lib/postgresql/data
              subPath: pgdata            # Use a subdirectory (avoids lost+found issues on ext4)
          env:
            - name: PGDATA
              value: /var/lib/postgresql/data/pgdata
      volumes:
        - name: data
          persistentVolumeClaim:
            claimName: postgres-data
```

### Storage Troubleshooting

```bash
# PVC stuck in Pending
kubectl describe pvc my-pvc
# Common causes:
# - No PV matches (static provisioning) — check size, access mode, storageClassName
# - StorageClass doesn't exist
# - CSI driver not installed
# - WaitForFirstConsumer — PVC won't bind until a pod uses it (this is normal)
# - Ran out of cloud storage quota

# PV stuck in Released (after PVC deleted)
kubectl get pv
# Fix: manually delete the PV or remove the claimRef to make it Available again
kubectl patch pv my-pv --type json -p '[{"op": "remove", "path": "/spec/claimRef"}]'

# Volume mount fails in pod
kubectl describe pod my-pod
# Look for: "Unable to attach or mount volumes"
# Common: volume is in a different zone than the node, volume still attached to another node
```

---

## 9. Namespaces — Multi-Tenancy

```bash
# Create namespace
kubectl create namespace production
kubectl create namespace staging

# Set default namespace for context
kubectl config set-context --current --namespace=production

# List resources in a namespace
kubectl get pods -n staging
kubectl get all -n production
```

### Resource Quotas

Caps total resource usage for an entire namespace. Think of it as a budget for the team.

```yaml
apiVersion: v1
kind: ResourceQuota
metadata:
  name: team-quota
  namespace: production
spec:
  hard:
    requests.cpu: "10"           # All pods combined can request max 10 CPU cores
    requests.memory: 20Gi        # All pods combined can request max 20 Gi
    limits.cpu: "20"             # All pods combined can have max 20 CPU limit
    limits.memory: 40Gi
    pods: "50"                   # Max 50 pods in this namespace
    services: "10"               # Max 10 services
    persistentvolumeclaims: "20" # Max 20 PVCs
```

If a namespace has a ResourceQuota, every pod must specify resource requests/limits — otherwise the pod is rejected. This prevents one team or namespace from hogging the cluster.

```bash
# Check quota usage
kubectl describe resourcequota team-quota -n production
# Shows: Used / Hard for each resource
```

### LimitRange — Default Limits for Pods

Sets defaults and constraints for individual containers/pods. Think of it as guardrails per pod.

```yaml
apiVersion: v1
kind: LimitRange
metadata:
  name: default-limits
  namespace: production
spec:
  limits:
    - type: Container
      default:                  # Applied as limits if pod doesn't specify
        cpu: "500m"
        memory: "256Mi"
      defaultRequest:           # Applied as requests if pod doesn't specify
        cpu: "100m"
        memory: "128Mi"
      max:                      # No container can exceed this
        cpu: "2"
        memory: "2Gi"
      min:                      # No container can go below this
        cpu: "50m"
        memory: "64Mi"
    - type: Pod
      max:
        cpu: "4"                # Total across all containers in a pod
        memory: "4Gi"
```

### How ResourceQuota and LimitRange Work Together

```
Developer deploys a pod with no resource specs
         │
         ▼
LimitRange kicks in → injects default requests (100m CPU, 128Mi)
                     → injects default limits (500m CPU, 256Mi)
         │
         ▼
ResourceQuota checks → "namespace total after this pod: 8.1 CPU requested out of 10 allowed"
                     → if under budget: pod is admitted
                     → if over budget: pod is REJECTED
```

| | ResourceQuota | LimitRange |
|---|---|---|
| Scope | Entire namespace (aggregate) | Individual container/pod |
| Purpose | Cap total usage | Set defaults and per-pod min/max |
| What happens | Rejects pod if namespace budget exceeded | Injects defaults, rejects if pod exceeds min/max |
| Required? | No, but recommended for multi-tenant clusters | No, but recommended when ResourceQuota is set |

Real-world pattern: Use them together. LimitRange ensures every pod has sane defaults so devs don't have to remember to set them. ResourceQuota ensures one namespace can't consume the whole cluster.

---

## 10. Scheduling — Controlling Pod Placement

When you create a pod, the kube-scheduler decides which node to place it on. The decision is based on resource availability, constraints, and preferences you configure. Here's the full scheduling flow:

```
New Pod created (no node assigned)
         │
         ▼
┌─────────────────────────────┐
│  1. FILTERING (Predicates)  │  Eliminate nodes that CAN'T run the pod
│                             │
│  - Enough CPU/memory?       │
│  - Does nodeSelector match? │
│  - Does node affinity match?│
│  - Are taints tolerated?    │
│  - Port conflicts?          │
│  - Volume zone matches?     │
└──────────┬──────────────────┘
           │ Remaining nodes
           ▼
┌─────────────────────────────┐
│  2. SCORING (Priorities)    │  Rank remaining nodes by preference
│                             │
│  - Spread pods evenly       │
│  - Preferred affinity match │
│  - Least resource usage     │
│  - Pod anti-affinity weight │
└──────────┬──────────────────┘
           │ Highest-scoring node
           ▼
     Pod is bound to node
```

If no node passes filtering, the pod stays in `Pending` status until a node becomes available (or Cluster Autoscaler adds one).

### Node Selectors (simple)

The simplest way to constrain a pod to specific nodes. Uses exact label matching — the node must have ALL specified labels.

```bash
# First, label your nodes
kubectl label nodes node1 disktype=ssd
kubectl label nodes node1 region=eu-west-1
kubectl label nodes node2 disktype=hdd
kubectl label nodes node2 region=eu-west-1

# Verify labels
kubectl get nodes --show-labels
```

```yaml
spec:
  nodeSelector:
    disktype: ssd              # Pod only runs on nodes with disktype=ssd
    region: eu-west-1          # AND region=eu-west-1 (both must match)
```

Limitation: nodeSelector only supports exact equality. You can't say "not hdd" or "either eu-west-1a or eu-west-1b". That's where node affinity comes in.

### Node Affinity (advanced)

Like nodeSelector but with expressive operators (In, NotIn, Exists, DoesNotExist, Gt, Lt) and soft/hard rules.

```yaml
spec:
  affinity:
    nodeAffinity:
      # HARD rule — pod will NOT be scheduled if no node matches
      requiredDuringSchedulingIgnoredDuringExecution:
        nodeSelectorTerms:
          - matchExpressions:
              - key: topology.kubernetes.io/zone    # Standard K8s zone label
                operator: In                         # Node must be in one of these zones
                values:
                  - eu-west-1a
                  - eu-west-1b
              - key: node.kubernetes.io/instance-type
                operator: NotIn                      # Node must NOT be these types
                values:
                  - t3.micro
                  - t3.small

      # SOFT rule — scheduler PREFERS these nodes but will use others if needed
      preferredDuringSchedulingIgnoredDuringExecution:
        - weight: 80                                 # Weight 1-100 (higher = stronger preference)
          preference:
            matchExpressions:
              - key: instance-type
                operator: In
                values:
                  - m5.xlarge
        - weight: 20                                 # Lower priority preference
          preference:
            matchExpressions:
              - key: disktype
                operator: In
                values:
                  - ssd
```

**Operators explained:**

| Operator | Meaning | Example |
|---|---|---|
| `In` | Label value is one of the listed values | zone In [eu-west-1a, eu-west-1b] |
| `NotIn` | Label value is NOT one of the listed values | instance-type NotIn [t3.micro] |
| `Exists` | Label key exists (value doesn't matter) | gpu Exists |
| `DoesNotExist` | Label key does NOT exist | spot DoesNotExist |
| `Gt` | Label value is greater than (numeric string) | memory Gt 64 |
| `Lt` | Label value is less than (numeric string) | cores Lt 16 |

**required vs preferred:**

| | required (hard) | preferred (soft) |
|---|---|---|
| If no node matches | Pod stays Pending | Pod scheduled on best available node |
| Use case | "Must be in zone eu-west-1a" | "Prefer m5.xlarge but anything works" |
| Risk | Pod might never schedule | Pod always schedules (just maybe not ideal) |

**"IgnoredDuringExecution"** means if the node labels change after the pod is running, the pod is NOT evicted. It only matters at scheduling time.

### Pod Affinity and Anti-Affinity

Node affinity places pods based on node labels. Pod affinity/anti-affinity places pods based on what OTHER pods are already running.

```yaml
spec:
  affinity:
    # Pod Affinity — "schedule me near pods with label app=redis"
    # Use case: co-locate app with its cache for low latency
    podAffinity:
      requiredDuringSchedulingIgnoredDuringExecution:
        - labelSelector:
            matchExpressions:
              - key: app
                operator: In
                values:
                  - redis
          topologyKey: kubernetes.io/hostname    # "Same node" as the redis pod
          # topologyKey: topology.kubernetes.io/zone  ← would mean "same zone"

    # Pod Anti-Affinity — "DON'T schedule me near pods with label app=web"
    # Use case: spread replicas across nodes for high availability
    podAntiAffinity:
      # Soft rule — prefer spreading but don't block scheduling
      preferredDuringSchedulingIgnoredDuringExecution:
        - weight: 100
          podAffinityTerm:
            labelSelector:
              matchLabels:
                app: web               # Don't co-locate with other web pods
            topologyKey: kubernetes.io/hostname  # "Different node"
```

**topologyKey** defines the scope of "near" or "away":

| topologyKey | Meaning |
|---|---|
| `kubernetes.io/hostname` | Same/different node |
| `topology.kubernetes.io/zone` | Same/different availability zone |
| `topology.kubernetes.io/region` | Same/different region |

**Common patterns:**

```yaml
# Spread web pods across zones (high availability)
podAntiAffinity:
  requiredDuringSchedulingIgnoredDuringExecution:
    - labelSelector:
        matchLabels:
          app: web
      topologyKey: topology.kubernetes.io/zone

# Co-locate frontend with backend on same node (low latency)
podAffinity:
  preferredDuringSchedulingIgnoredDuringExecution:
    - weight: 50
      podAffinityTerm:
        labelSelector:
          matchLabels:
            app: backend
        topologyKey: kubernetes.io/hostname
```

### Topology Spread Constraints

More fine-grained than pod anti-affinity for evenly distributing pods.

```yaml
spec:
  topologySpreadConstraints:
    - maxSkew: 1                          # Max difference in pod count between zones
      topologyKey: topology.kubernetes.io/zone
      whenUnsatisfiable: DoNotSchedule    # DoNotSchedule or ScheduleAnyway
      labelSelector:
        matchLabels:
          app: web
    - maxSkew: 1                          # Also spread across nodes within each zone
      topologyKey: kubernetes.io/hostname
      whenUnsatisfiable: ScheduleAnyway   # Soft — best effort
      labelSelector:
        matchLabels:
          app: web
```

Example: 3 zones, 6 replicas → each zone gets exactly 2 pods (maxSkew: 1 ensures no zone has more than 1 extra).

### Taints and Tolerations

Taints are the opposite of affinity — they REPEL pods from nodes. Only pods with a matching toleration can be scheduled on a tainted node.

```
Node affinity:    Pod says "I want to run HERE"      (pod attracts to node)
Taints:           Node says "Go away UNLESS you tolerate me"  (node repels pods)
```

```bash
# Taint a node — repels all pods unless they tolerate it
kubectl taint nodes node1 dedicated=gpu:NoSchedule

# View taints on a node
kubectl describe node node1 | grep Taints

# Remove a taint (note the trailing dash)
kubectl taint nodes node1 dedicated=gpu:NoSchedule-
```

```yaml
# Pod that tolerates the taint — CAN be scheduled on node1
spec:
  tolerations:
    - key: "dedicated"          # Must match the taint key
      operator: "Equal"         # "Equal" (key+value must match) or "Exists" (key only)
      value: "gpu"              # Must match the taint value (when operator is Equal)
      effect: "NoSchedule"      # Must match the taint effect

    # Wildcard — tolerate everything (use with caution)
    # - operator: "Exists"      # No key = matches all taints
```

**Taint effects:**

| Taint Effect | Behavior | Use Case |
|---|---|---|
| `NoSchedule` | New pods won't be scheduled (existing pods stay) | Reserve nodes for specific workloads |
| `PreferNoSchedule` | Scheduler avoids but will use if no other option | Soft reservation |
| `NoExecute` | Evicts existing pods AND prevents new ones | Node maintenance, node problems |

**NoExecute with tolerationSeconds:**

```yaml
tolerations:
  - key: "node.kubernetes.io/unreachable"
    operator: "Exists"
    effect: "NoExecute"
    tolerationSeconds: 300     # Pod stays for 300s after taint is applied, then evicted
```

**Built-in taints** (automatically applied by Kubernetes):

| Taint | When Applied |
|---|---|
| `node.kubernetes.io/not-ready` | Node is not ready |
| `node.kubernetes.io/unreachable` | Node is unreachable from controller |
| `node.kubernetes.io/memory-pressure` | Node has low memory |
| `node.kubernetes.io/disk-pressure` | Node has low disk |
| `node.kubernetes.io/pid-pressure` | Node has too many processes |
| `node.kubernetes.io/unschedulable` | Node is cordoned (`kubectl cordon`) |

### Putting It All Together — Scheduling Decision Matrix

| Mechanism | Direction | Scope | Use Case |
|---|---|---|---|
| **nodeSelector** | Pod → Node | Simple label match | "Run on SSD nodes" |
| **Node Affinity** | Pod → Node | Expressive rules (In, NotIn, Gt, Lt) | "Prefer m5.xlarge in eu-west-1a" |
| **Pod Affinity** | Pod → Pod | Co-locate with other pods | "Run near the redis pods" |
| **Pod Anti-Affinity** | Pod ↔ Pod | Separate from other pods | "Spread web replicas across nodes" |
| **Topology Spread** | Pod ↔ Pod | Even distribution | "Equal pods per zone" |
| **Taints/Tolerations** | Node → Pod | Node repels pods | "Only GPU workloads on GPU nodes" |

---

## 11. Auto-Scaling

### Horizontal Pod Autoscaler (HPA)

```yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: web-hpa
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: web-app
  minReplicas: 2
  maxReplicas: 20
  metrics:
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: 70
    - type: Resource
      resource:
        name: memory
        target:
          type: Utilization
          averageUtilization: 80
  behavior:
    scaleDown:
      stabilizationWindowSeconds: 300   # Wait 5 min before scaling down
      policies:
        - type: Percent
          value: 10
          periodSeconds: 60
    scaleUp:
      stabilizationWindowSeconds: 0
      policies:
        - type: Percent
          value: 100
          periodSeconds: 15
```

```bash
# Quick HPA creation
kubectl autoscale deployment web-app --min=2 --max=20 --cpu-percent=70

# Check HPA status
kubectl get hpa
```

### Vertical Pod Autoscaler (VPA)

```yaml
apiVersion: autoscaling.k8s.io/v1
kind: VerticalPodAutoscaler
metadata:
  name: web-vpa
spec:
  targetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: web-app
  updatePolicy:
    updateMode: "Auto"        # Auto, Recreate, Initial, Off
  resourcePolicy:
    containerPolicies:
      - containerName: web
        minAllowed:
          cpu: "100m"
          memory: "128Mi"
        maxAllowed:
          cpu: "4"
          memory: "8Gi"
```

### Cluster Autoscaler

Automatically adjusts the number of nodes. Configured at the cloud provider level.

```yaml
# AWS EKS — managed node group with auto-scaling
apiVersion: eksctl.io/v1alpha5
kind: ClusterConfig
metadata:
  name: my-cluster
  region: eu-west-1
managedNodeGroups:
  - name: workers
    instanceType: m5.xlarge
    minSize: 2
    maxSize: 10
    desiredCapacity: 3
```

---

## 12. RBAC — Role-Based Access Control

### Role (namespace-scoped) and ClusterRole (cluster-wide)

```yaml
# Role — access within a namespace
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata:
  name: developer
  namespace: development
rules:
  - apiGroups: [""]
    resources: ["pods", "services", "configmaps"]
    verbs: ["get", "list", "watch", "create", "update", "delete"]
  - apiGroups: ["apps"]
    resources: ["deployments"]
    verbs: ["get", "list", "watch", "create", "update"]
  - apiGroups: [""]
    resources: ["secrets"]
    verbs: ["get", "list"]       # Read-only for secrets

---
# ClusterRole — cluster-wide
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRole
metadata:
  name: log-reader
rules:
  - apiGroups: [""]
    resources: ["pods/log"]
    verbs: ["get", "list"]
```

### Binding Roles to Users/Groups

```yaml
# RoleBinding — binds Role to user in a namespace
apiVersion: rbac.authorization.k8s.io/v1
kind: RoleBinding
metadata:
  name: developer-binding
  namespace: development
subjects:
  - kind: User
    name: jane@company.com
    apiGroup: rbac.authorization.k8s.io
  - kind: Group
    name: dev-team
    apiGroup: rbac.authorization.k8s.io
roleRef:
  kind: Role
  name: developer
  apiGroup: rbac.authorization.k8s.io

---
# ClusterRoleBinding
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: log-reader-binding
subjects:
  - kind: Group
    name: all-developers
    apiGroup: rbac.authorization.k8s.io
roleRef:
  kind: ClusterRole
  name: log-reader
  apiGroup: rbac.authorization.k8s.io
```

### Service Accounts

```yaml
apiVersion: v1
kind: ServiceAccount
metadata:
  name: app-sa
  namespace: production
  annotations:
    # AWS IRSA — pod assumes IAM role
    eks.amazonaws.com/role-arn: arn:aws:iam::123456789:role/app-role

---
# Use in a pod
spec:
  serviceAccountName: app-sa
  containers:
    - name: app
      image: myapp:1.0
```

---


## 13. Probes — Health Checks

```yaml
spec:
  containers:
    - name: app
      image: myapp:1.0

      # Startup Probe — gives slow-starting containers time to initialize
      # Blocks liveness and readiness probes until it succeeds
      startupProbe:
        httpGet:
          path: /healthz           # HTTP endpoint to check
          port: 8080               # Port to send the request to
        failureThreshold: 30       # Number of consecutive failures before killing the container
        periodSeconds: 10          # How often (in seconds) to perform the probe
        # Total startup budget = failureThreshold × periodSeconds = 30 × 10 = 300 seconds

      # Liveness Probe — restarts container if it becomes unhealthy
      # Use when your app can deadlock or hang without crashing
      livenessProbe:
        httpGet:
          path: /healthz           # Endpoint that returns 200-399 for healthy
          port: 8080               # Port the app is listening on
        initialDelaySeconds: 0     # Seconds to wait after container starts before first probe (0 because startupProbe handles startup)
        periodSeconds: 15          # Run the probe every 15 seconds
        timeoutSeconds: 3          # Probe times out after 3 seconds (counts as failure)
        failureThreshold: 3        # Container is restarted after 3 consecutive failures
        successThreshold: 1        # 1 success is enough to mark as healthy again (always 1 for liveness)

      # Readiness Probe — removes from service endpoints if not ready
      # Use when your app needs time to load data, warm cache, or handle temporary load
      readinessProbe:
        httpGet:
          path: /ready             # Can be a different endpoint than liveness (e.g., checks DB connection)
          port: 8080               # Port to probe
        initialDelaySeconds: 0     # Seconds to wait before first probe
        periodSeconds: 5           # Check every 5 seconds (more frequent than liveness — traffic routing matters)
        timeoutSeconds: 2          # Probe times out after 2 seconds
        failureThreshold: 3        # Removed from service endpoints after 3 consecutive failures
        successThreshold: 1        # Added back to endpoints after 1 success
```

### Probe Types

```yaml
# HTTP GET — most common, checks an HTTP endpoint
# Success: response code 200-399
livenessProbe:
  httpGet:
    path: /health                  # URL path to request
    port: 8080                     # Port to connect to
    scheme: HTTP                   # HTTP or HTTPS (default: HTTP)
    httpHeaders:                   # Optional custom headers
      - name: Authorization
        value: Bearer token123

# TCP Socket — just checks if the port is open
# Success: TCP connection established
# Use for non-HTTP services (databases, message queues)
livenessProbe:
  tcpSocket:
    port: 5432                     # Port to attempt TCP connection on

# Command execution — runs a command inside the container
# Success: command exits with code 0
# Use when health can't be checked via network
livenessProbe:
  exec:
    command:                       # Command and arguments to run
      - cat
      - /tmp/healthy               # Exits 0 if file exists, non-zero otherwise

# gRPC — native gRPC health check protocol
# Success: gRPC health check returns SERVING
# Requires app to implement grpc.health.v1.Health service
livenessProbe:
  grpc:
    port: 50051                    # gRPC server port
    service: ""                    # Service name to check (empty = overall server health)
```

| Probe | Purpose | Failure Action |
|---|---|---|
| **Startup** | Is the app started yet? | Restarts container (blocks other probes until pass) |
| **Liveness** | Is the app alive? | Restarts container |
| **Readiness** | Can the app serve traffic? | Removes from service endpoints (no restart) |

---

## 14. Helm — Kubernetes Package Manager

Helm packages Kubernetes manifests into reusable, versioned charts.

```bash
# Install Helm
curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash

# Add a chart repository
helm repo add bitnami https://charts.bitnami.com/bitnami
helm repo update

# Search for charts
helm search repo postgres

# Install a chart
helm install my-postgres bitnami/postgresql \
  --namespace databases \
  --create-namespace \
  --set auth.postgresPassword=secret123 \
  --set primary.persistence.size=20Gi

# List releases
helm list -A

# Upgrade
helm upgrade my-postgres bitnami/postgresql --set primary.resources.limits.memory=1Gi

# Rollback
helm rollback my-postgres 1

# Uninstall
helm uninstall my-postgres -n databases
```

### Creating Your Own Helm Chart

```bash
helm create myapp
```

Produces this structure:

```
myapp/
├── Chart.yaml             # Chart metadata
├── values.yaml            # Default configuration values
├── templates/
│   ├── deployment.yaml
│   ├── service.yaml
│   ├── ingress.yaml
│   ├── hpa.yaml
│   ├── serviceaccount.yaml
│   ├── _helpers.tpl       # Template helpers
│   └── NOTES.txt          # Post-install notes
└── charts/                # Dependencies
```

### values.yaml

```yaml
replicaCount: 3

image:
  repository: myapp
  tag: "1.0"
  pullPolicy: IfNotPresent

service:
  type: ClusterIP
  port: 80

ingress:
  enabled: true
  host: app.example.com

resources:
  requests:
    cpu: 100m
    memory: 128Mi
  limits:
    cpu: 500m
    memory: 256Mi

autoscaling:
  enabled: true
  minReplicas: 2
  maxReplicas: 10
  targetCPUUtilization: 70
```

### templates/deployment.yaml (templated)

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ include "myapp.fullname" . }}
  labels:
    {{- include "myapp.labels" . | nindent 4 }}
spec:
  replicas: {{ .Values.replicaCount }}
  selector:
    matchLabels:
      {{- include "myapp.selectorLabels" . | nindent 6 }}
  template:
    metadata:
      labels:
        {{- include "myapp.selectorLabels" . | nindent 8 }}
    spec:
      containers:
        - name: {{ .Chart.Name }}
          image: "{{ .Values.image.repository }}:{{ .Values.image.tag }}"
          ports:
            - containerPort: 8080
          resources:
            {{- toYaml .Values.resources | nindent 12 }}
```

```bash
# Install with custom values
helm install myrelease ./myapp -f production-values.yaml

# Template locally (dry run — see generated manifests)
helm template myrelease ./myapp -f production-values.yaml

# Lint
helm lint ./myapp
```

---

## 15. Monitoring and Logging

### Prometheus + Grafana Stack

```bash
# Install via Helm
helm repo add prometheus-community https://prometheus-community.github.io/helm-charts
helm install monitoring prometheus-community/kube-prometheus-stack \
  --namespace monitoring \
  --create-namespace
```

### Pod metrics with annotations

```yaml
metadata:
  annotations:
    prometheus.io/scrape: "true"
    prometheus.io/port: "8080"
    prometheus.io/path: "/metrics"
```

### Key kubectl commands for monitoring

```bash
# Resource usage
kubectl top nodes
kubectl top pods -A
kubectl top pods --containers

# Events (debugging)
kubectl get events --sort-by='.lastTimestamp'
kubectl get events -n production --field-selector type=Warning

# Logs
kubectl logs my-pod
kubectl logs my-pod -c sidecar-container     # Specific container
kubectl logs -f my-pod                        # Follow
kubectl logs --previous my-pod                # Previous crashed container
kubectl logs -l app=web --all-containers      # All pods with label
```

### EFK/ELK Stack for Centralized Logging

```yaml
# Fluent Bit DaemonSet — collects logs from every node
apiVersion: apps/v1
kind: DaemonSet
metadata:
  name: fluent-bit
  namespace: logging
spec:
  selector:
    matchLabels:
      app: fluent-bit
  template:
    metadata:
      labels:
        app: fluent-bit
    spec:
      serviceAccountName: fluent-bit
      containers:
        - name: fluent-bit
          image: fluent/fluent-bit:latest
          volumeMounts:
            - name: varlog
              mountPath: /var/log
            - name: containers
              mountPath: /var/lib/docker/containers
              readOnly: true
      volumes:
        - name: varlog
          hostPath:
            path: /var/log
        - name: containers
          hostPath:
            path: /var/lib/docker/containers
```

---

## 16. Pod Disruption Budgets (PDB)

Ensures a minimum number of pods remain available during voluntary disruptions (node drain, cluster upgrade).

```yaml
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: web-pdb
spec:
  minAvailable: 2              # At least 2 pods must be running
  # OR
  # maxUnavailable: 1          # At most 1 pod can be unavailable
  selector:
    matchLabels:
      app: web
```

---

## 17. kubectl Cheat Sheet

```bash
# Cluster info
kubectl cluster-info
kubectl get nodes -o wide
kubectl api-resources                  # List all resource types
kubectl explain deployment.spec        # Documentation for a field

# Get resources
kubectl get pods -A                    # All namespaces
kubectl get pods -o wide               # Extra info (node, IP)
kubectl get pods -o yaml               # Full YAML output
kubectl get pods -l app=web            # Filter by label
kubectl get pods --field-selector status.phase=Running
kubectl get all -n production          # All resources in namespace

# Describe (detailed info + events)
kubectl describe pod my-pod
kubectl describe node my-node

# Create/Apply
kubectl apply -f manifest.yaml         # Declarative (idempotent)
kubectl create -f manifest.yaml        # Imperative (errors if exists)
kubectl apply -f ./k8s/                # Apply all files in directory
kubectl apply -f https://url/manifest.yaml  # From URL

# Edit live
kubectl edit deployment web-app        # Opens in editor

# Delete
kubectl delete -f manifest.yaml
kubectl delete pod my-pod
kubectl delete pods -l app=test        # Delete by label

# Exec into pod
kubectl exec -it my-pod -- bash
kubectl exec -it my-pod -c sidecar -- sh  # Specific container

# Port forward (local debugging)
kubectl port-forward svc/my-service 8080:80
kubectl port-forward pod/my-pod 5432:5432

# Copy files
kubectl cp my-pod:/app/log.txt ./log.txt
kubectl cp ./config.json my-pod:/app/config.json

# Labels
kubectl label pods my-pod env=production
kubectl label pods my-pod env-                 # Remove label

# Drain node (for maintenance)
kubectl cordon node1                   # Mark unschedulable
kubectl drain node1 --ignore-daemonsets --delete-emptydir-data
kubectl uncordon node1                 # Mark schedulable again

# Rollouts
kubectl rollout status deployment/web
kubectl rollout history deployment/web
kubectl rollout undo deployment/web
kubectl rollout restart deployment/web  # Rolling restart

# Debugging
kubectl get events --sort-by='.lastTimestamp'
kubectl debug -it my-pod --image=busybox --target=my-container
```

---

## 18. Azure AKS — Managed Kubernetes

### Create cluster with Azure CLI

```bash
# Install Azure CLI
# https://learn.microsoft.com/en-us/cli/azure/install-azure-cli

# Login
az login

# Create resource group
az group create --name myResourceGroup --location westeurope

# Create AKS cluster
az aks create \
  --resource-group myResourceGroup \
  --name my-aks-cluster \
  --node-count 3 \
  --node-vm-size Standard_D4s_v3 \
  --kubernetes-version 1.29 \
  --network-plugin azure \
  --enable-managed-identity \
  --generate-ssh-keys

# Get kubeconfig
az aks get-credentials --resource-group myResourceGroup --name my-aks-cluster
```

### AKS with Node Pools

AKS supports multiple node pools with different VM sizes, scaling rules, and OS types.

```bash
# Add a GPU node pool
az aks nodepool add \
  --resource-group myResourceGroup \
  --cluster-name my-aks-cluster \
  --name gpupool \
  --node-count 1 \
  --node-vm-size Standard_NC6s_v3 \
  --node-taints sku=gpu:NoSchedule \
  --labels hardware=gpu

# Add a spot (preemptible) node pool for cost savings
az aks nodepool add \
  --resource-group myResourceGroup \
  --cluster-name my-aks-cluster \
  --name spotpool \
  --node-count 3 \
  --node-vm-size Standard_D4s_v3 \
  --priority Spot \
  --eviction-policy Delete \
  --spot-max-price -1 \
  --labels kubernetes.azure.com/scalesetpriority=spot

# Scale a node pool
az aks nodepool scale \
  --resource-group myResourceGroup \
  --cluster-name my-aks-cluster \
  --name nodepool1 \
  --node-count 5

# Enable cluster autoscaler on a node pool
az aks nodepool update \
  --resource-group myResourceGroup \
  --cluster-name my-aks-cluster \
  --name nodepool1 \
  --enable-cluster-autoscaler \
  --min-count 2 \
  --max-count 10
```

### AKS Networking — Azure CNI vs Kubenet

| Feature | Azure CNI | Kubenet |
|---|---|---|
| Pod IPs | From VNet subnet (routable) | Private, NAT to node IP |
| Performance | Better (no overlay) | Extra hop |
| IP usage | Consumes VNet IPs (plan subnet size) | Efficient |
| Network Policies | Azure + Calico | Calico only |
| Use case | Production, hybrid connectivity | Dev/test, small clusters |

```bash
# Azure CNI (recommended for production)
az aks create \
  --resource-group myResourceGroup \
  --name my-aks-cluster \
  --network-plugin azure \
  --vnet-subnet-id /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Network/virtualNetworks/<vnet>/subnets/<subnet> \
  --service-cidr 10.0.0.0/16 \
  --dns-service-ip 10.0.0.10

# Azure CNI Overlay (pods get overlay IPs, saves VNet IPs)
az aks create \
  --resource-group myResourceGroup \
  --name my-aks-cluster \
  --network-plugin azure \
  --network-plugin-mode overlay \
  --pod-cidr 192.168.0.0/16
```

### Workload Identity (replaces Pod Identity)

Workload Identity lets pods authenticate to Azure services (Key Vault, Storage, SQL) using Azure AD without storing credentials.

```bash
# Enable workload identity on cluster
az aks update \
  --resource-group myResourceGroup \
  --name my-aks-cluster \
  --enable-oidc-issuer \
  --enable-workload-identity

# Get OIDC issuer URL
export AKS_OIDC_ISSUER=$(az aks show \
  --resource-group myResourceGroup \
  --name my-aks-cluster \
  --query "oidcIssuerProfile.issuerUrl" -o tsv)

# Create managed identity
az identity create \
  --name app-identity \
  --resource-group myResourceGroup

export IDENTITY_CLIENT_ID=$(az identity show \
  --name app-identity \
  --resource-group myResourceGroup \
  --query clientId -o tsv)

# Create federated credential (links K8s SA to Azure identity)
az identity federated-credential create \
  --name app-federated-cred \
  --identity-name app-identity \
  --resource-group myResourceGroup \
  --issuer $AKS_OIDC_ISSUER \
  --subject system:serviceaccount:production:app-sa \
  --audience api://AzureADTokenExchange

# Grant the identity access to Azure resources
az keyvault set-policy \
  --name my-keyvault \
  --object-id $(az identity show --name app-identity --resource-group myResourceGroup --query principalId -o tsv) \
  --secret-permissions get list
```

```yaml
# Kubernetes ServiceAccount annotated with workload identity
apiVersion: v1
kind: ServiceAccount
metadata:
  name: app-sa
  namespace: production
  annotations:
    azure.workload.identity/client-id: "<IDENTITY_CLIENT_ID>"
  labels:
    azure.workload.identity/use: "true"

---
# Pod using the service account — gets Azure credentials automatically
apiVersion: apps/v1
kind: Deployment
metadata:
  name: app
  namespace: production
spec:
  selector:
    matchLabels:
      app: myapp
  template:
    metadata:
      labels:
        app: myapp
    spec:
      serviceAccountName: app-sa
      containers:
        - name: app
          image: myapp:1.0
          # Azure SDK automatically picks up credentials via workload identity
```

### Azure Container Registry (ACR) Integration

```bash
# Create ACR
az acr create --resource-group myResourceGroup --name myacr --sku Standard

# Attach ACR to AKS (grants pull access)
az aks update \
  --resource-group myResourceGroup \
  --name my-aks-cluster \
  --attach-acr myacr

# Build and push image to ACR
az acr login --name myacr
docker tag myapp:1.0 myacr.azurecr.io/myapp:1.0
docker push myacr.azurecr.io/myapp:1.0
```

```yaml
# Use ACR image in deployment (no imagePullSecrets needed after attach)
spec:
  containers:
    - name: app
      image: myacr.azurecr.io/myapp:1.0
```

### Azure Disk and Azure Files for Storage

```yaml
# StorageClass — Azure Disk (ReadWriteOnce)
apiVersion: storage.k8s.io/v1
kind: StorageClass
metadata:
  name: managed-premium
provisioner: disk.csi.azure.com
parameters:
  skuName: Premium_LRS       # Premium_LRS, StandardSSD_LRS, Standard_LRS
reclaimPolicy: Delete
volumeBindingMode: WaitForFirstConsumer
allowVolumeExpansion: true

---
# StorageClass — Azure Files (ReadWriteMany — shared across pods)
apiVersion: storage.k8s.io/v1
kind: StorageClass
metadata:
  name: azurefile-premium
provisioner: file.csi.azure.com
parameters:
  skuName: Premium_LRS
reclaimPolicy: Delete
mountOptions:
  - dir_mode=0777
  - file_mode=0777
allowVolumeExpansion: true
```

### AKS with Azure Key Vault — Secrets Store CSI Driver

```bash
# Enable the addon
az aks enable-addons \
  --resource-group myResourceGroup \
  --name my-aks-cluster \
  --addons azure-keyvault-secrets-provider
```

```yaml
# SecretProviderClass — mounts Key Vault secrets as files/env vars
apiVersion: secrets-store.csi.x-k8s.io/v1
kind: SecretProviderClass
metadata:
  name: azure-kv-secrets
  namespace: production
spec:
  provider: azure
  parameters:
    usePodIdentity: "false"
    useVMManagedIdentity: "false"
    clientID: "<IDENTITY_CLIENT_ID>"
    keyvaultName: "my-keyvault"
    objects: |
      array:
        - |
          objectName: db-password
          objectType: secret
        - |
          objectName: api-key
          objectType: secret
    tenantId: "<TENANT_ID>"
  secretObjects:                    # Sync to Kubernetes Secret
    - secretName: app-secrets
      type: Opaque
      data:
        - objectName: db-password
          key: DATABASE_PASSWORD
        - objectName: api-key
          key: API_KEY

---
# Pod mounting the secrets
spec:
  serviceAccountName: app-sa
  containers:
    - name: app
      image: myapp:1.0
      env:
        - name: DATABASE_PASSWORD
          valueFrom:
            secretKeyRef:
              name: app-secrets
              key: DATABASE_PASSWORD
      volumeMounts:
        - name: secrets
          mountPath: /mnt/secrets
          readOnly: true
  volumes:
    - name: secrets
      csi:
        driver: secrets-store.csi.k8s.io
        readOnly: true
        volumeAttributes:
          secretProviderClass: azure-kv-secrets
```

### AKS Monitoring — Azure Monitor and Container Insights

```bash
# Enable monitoring addon
az aks enable-addons \
  --resource-group myResourceGroup \
  --name my-aks-cluster \
  --addons monitoring \
  --workspace-resource-id /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.OperationalInsights/workspaces/<workspace>
```

Key Kusto (KQL) queries in Log Analytics:

```kusto
// Pods in CrashLoopBackOff
KubePodInventory
| where PodStatus == "Failed"
| summarize count() by Name, Namespace, PodStatus
| order by count_ desc

// Container memory usage over time
Perf
| where ObjectName == "K8SContainer" and CounterName == "memoryRssBytes"
| summarize avg(CounterValue) by bin(TimeGenerated, 5m), InstanceName
| render timechart

// OOMKilled events
ContainerLog
| where LogEntry contains "OOMKilled"
| project TimeGenerated, ContainerID, LogEntry
```

### AKS Useful Commands

```bash
# Get cluster info
az aks show --resource-group myResourceGroup --name my-aks-cluster

# Upgrade cluster
az aks get-upgrades --resource-group myResourceGroup --name my-aks-cluster
az aks upgrade --resource-group myResourceGroup --name my-aks-cluster --kubernetes-version 1.29

# Start/Stop cluster (save costs for dev clusters)
az aks stop --resource-group myResourceGroup --name my-aks-cluster
az aks start --resource-group myResourceGroup --name my-aks-cluster

# Enable HTTP application routing (dev ingress)
az aks enable-addons --resource-group myResourceGroup --name my-aks-cluster --addons http_application_routing

# Get node resource usage
kubectl top nodes
kubectl describe node <node-name> | grep -A 5 "Allocated resources"
```

### AKS vs EKS Comparison

| Feature | AKS | EKS |
|---|---|---|
| Control plane cost | Free | ~$0.10/hr per cluster |
| Default CNI | Azure CNI | Amazon VPC CNI |
| Serverless pods | ACI (Virtual Nodes) | Fargate |
| Identity for pods | Workload Identity | IRSA |
| Secret integration | Key Vault CSI Driver | Secrets Manager + ESO |
| Registry | ACR (attach to cluster) | ECR (IRSA or node role) |
| Monitoring | Container Insights (built-in) | CloudWatch Container Insights |
| Node autoscaler | Cluster Autoscaler / KEDA | Cluster Autoscaler / Karpenter |
| Service mesh | Istio addon / Open Service Mesh | App Mesh / Istio |

---

## 19. AWS EKS — Managed Kubernetes

### Create cluster with eksctl

```bash
# Install eksctl
# https://eksctl.io

# Create cluster
eksctl create cluster \
  --name my-cluster \
  --region eu-west-1 \
  --version 1.29 \
  --nodegroup-name workers \
  --node-type m5.xlarge \
  --nodes 3 \
  --nodes-min 2 \
  --nodes-max 10 \
  --managed

# Update kubeconfig
aws eks update-kubeconfig --name my-cluster --region eu-west-1
```

### EKS with Fargate (serverless pods)

```yaml
apiVersion: eksctl.io/v1alpha5
kind: ClusterConfig
metadata:
  name: my-cluster
  region: eu-west-1
fargateProfiles:
  - name: default
    selectors:
      - namespace: production
        labels:
          compute: fargate
```

### IRSA — IAM Roles for Service Accounts

```bash
# Create OIDC provider
eksctl utils associate-iam-oidc-provider --cluster my-cluster --approve

# Create service account with IAM role
eksctl create iamserviceaccount \
  --name s3-reader \
  --namespace production \
  --cluster my-cluster \
  --attach-policy-arn arn:aws:iam::aws:policy/AmazonS3ReadOnlyAccess \
  --approve
```

```yaml
# Pod uses the service account — gets AWS credentials automatically
spec:
  serviceAccountName: s3-reader
  containers:
    - name: app
      image: myapp:1.0
      # AWS SDK automatically picks up credentials via IRSA
```

---

## 20. Real-World Example — Full Application Stack

```yaml
# namespace.yaml
apiVersion: v1
kind: Namespace
metadata:
  name: production
  labels:
    env: production

---
# configmap.yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: app-config
  namespace: production
data:
  DATABASE_HOST: "postgres"
  DATABASE_PORT: "5432"
  DATABASE_NAME: "myapp"
  REDIS_HOST: "redis"

---
# secret.yaml
apiVersion: v1
kind: Secret
metadata:
  name: app-secrets
  namespace: production
type: Opaque
stringData:
  DATABASE_PASSWORD: "super-secret-password"
  JWT_SECRET: "jwt-signing-key"

---
# postgres.yaml
apiVersion: apps/v1
kind: StatefulSet
metadata:
  name: postgres
  namespace: production
spec:
  serviceName: postgres
  replicas: 1
  selector:
    matchLabels:
      app: postgres
  template:
    metadata:
      labels:
        app: postgres
    spec:
      containers:
        - name: postgres
          image: postgres:15-alpine
          ports:
            - containerPort: 5432
          env:
            - name: POSTGRES_DB
              valueFrom:
                configMapKeyRef:
                  name: app-config
                  key: DATABASE_NAME
            - name: POSTGRES_PASSWORD
              valueFrom:
                secretKeyRef:
                  name: app-secrets
                  key: DATABASE_PASSWORD
          volumeMounts:
            - name: postgres-data
              mountPath: /var/lib/postgresql/data
          readinessProbe:
            exec:
              command: ["pg_isready", "-U", "postgres"]
            periodSeconds: 5
  volumeClaimTemplates:
    - metadata:
        name: postgres-data
      spec:
        accessModes: ["ReadWriteOnce"]
        resources:
          requests:
            storage: 10Gi

---
apiVersion: v1
kind: Service
metadata:
  name: postgres
  namespace: production
spec:
  clusterIP: None
  selector:
    app: postgres
  ports:
    - port: 5432

---
# redis.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: redis
  namespace: production
spec:
  replicas: 1
  selector:
    matchLabels:
      app: redis
  template:
    metadata:
      labels:
        app: redis
    spec:
      containers:
        - name: redis
          image: redis:7-alpine
          ports:
            - containerPort: 6379
          resources:
            requests:
              cpu: 100m
              memory: 128Mi
            limits:
              cpu: 250m
              memory: 256Mi

---
apiVersion: v1
kind: Service
metadata:
  name: redis
  namespace: production
spec:
  selector:
    app: redis
  ports:
    - port: 6379

---
# api-deployment.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: api
  namespace: production
spec:
  replicas: 3
  selector:
    matchLabels:
      app: api
  strategy:
    rollingUpdate:
      maxSurge: 1
      maxUnavailable: 0
  template:
    metadata:
      labels:
        app: api
    spec:
      containers:
        - name: api
          image: myapi:2.0
          ports:
            - containerPort: 8080
          envFrom:
            - configMapRef:
                name: app-config
          env:
            - name: DATABASE_PASSWORD
              valueFrom:
                secretKeyRef:
                  name: app-secrets
                  key: DATABASE_PASSWORD
          resources:
            requests:
              cpu: 200m
              memory: 256Mi
            limits:
              cpu: "1"
              memory: 512Mi
          startupProbe:
            httpGet:
              path: /health
              port: 8080
            failureThreshold: 30
            periodSeconds: 10
          livenessProbe:
            httpGet:
              path: /health
              port: 8080
            periodSeconds: 15
          readinessProbe:
            httpGet:
              path: /ready
              port: 8080
            periodSeconds: 5

---
apiVersion: v1
kind: Service
metadata:
  name: api
  namespace: production
spec:
  selector:
    app: api
  ports:
    - port: 80
      targetPort: 8080

---
# hpa.yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: api-hpa
  namespace: production
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: api
  minReplicas: 3
  maxReplicas: 15
  metrics:
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: 70

---
# pdb.yaml
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: api-pdb
  namespace: production
spec:
  minAvailable: 2
  selector:
    matchLabels:
      app: api

---
# ingress.yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: api-ingress
  namespace: production
  annotations:
    nginx.ingress.kubernetes.io/rate-limit: "100"
    cert-manager.io/cluster-issuer: letsencrypt-prod
spec:
  ingressClassName: nginx
  tls:
    - hosts:
        - api.example.com
      secretName: api-tls
  rules:
    - host: api.example.com
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: api
                port:
                  number: 80

---
# network-policy.yaml
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: api-netpol
  namespace: production
spec:
  podSelector:
    matchLabels:
      app: api
  policyTypes:
    - Ingress
    - Egress
  ingress:
    - from:
        - namespaceSelector:
            matchLabels:
              kubernetes.io/metadata.name: ingress-nginx
      ports:
        - port: 8080
  egress:
    - to:
        - podSelector:
            matchLabels:
              app: postgres
      ports:
        - port: 5432
    - to:
        - podSelector:
            matchLabels:
              app: redis
      ports:
        - port: 6379
    - to:
        - namespaceSelector: {}
      ports:
        - port: 53
          protocol: UDP
```

Deploy everything:
```bash
kubectl apply -f namespace.yaml
kubectl apply -f . -n production
kubectl get all -n production
```

---

## 21. Interview Scenarios

---

### Scenario 1: Pod Stuck in Pending State

**Q: Your pod is stuck in `Pending` status for 10 minutes. How do you troubleshoot?**

```bash
kubectl describe pod my-pod
# Check the Events section at the bottom
```

Common causes and fixes:

| Event Message | Cause | Fix |
|---|---|---|
| `Insufficient cpu/memory` | Nodes don't have enough resources | Reduce resource requests, add nodes, or check if other pods are over-requesting |
| `0/3 nodes are available: 3 node(s) had taint` | All nodes have taints the pod doesn't tolerate | Add toleration or remove taint |
| `no persistent volumes available` | PVC can't bind to a PV | Check StorageClass, PV availability, zone mismatch |
| `FailedScheduling` | No node matches nodeSelector/affinity | Check node labels: `kubectl get nodes --show-labels` |

---

### Scenario 2: Pod in CrashLoopBackOff

**Q: Your pod keeps restarting with status `CrashLoopBackOff`. Diagnosis steps?**

```bash
# Step 1: Check logs
kubectl logs my-pod --previous     # Logs from the crashed instance

# Step 2: Check events
kubectl describe pod my-pod

# Step 3: Check exit code
kubectl get pod my-pod -o jsonpath='{.status.containerStatuses[0].lastState.terminated.exitCode}'
# Exit code 1 = app error, 137 = OOMKilled (128 + 9 SIGKILL), 143 = SIGTERM

# Step 4: Run interactively to debug
kubectl run debug --image=myapp:1.0 -it --rm -- sh

# Step 5: Check if it's a probe issue
# A failing liveness probe restarts the container
```

Common causes:
- Application crash (missing env vars, config, dependencies)
- OOMKilled (exit 137) — increase memory limits
- Liveness probe failing too aggressively — increase `initialDelaySeconds`
- Entrypoint/CMD error — wrong command or missing binary

---

### Scenario 3: Service Not Reaching Pods

**Q: Your Service exists but returns "connection refused" or "no endpoints". How do you debug?**

```bash
# Step 1: Check endpoints
kubectl get endpoints my-service
# If ENDPOINTS is <none>, no pods match the selector

# Step 2: Verify label match
kubectl get pods --show-labels
kubectl get svc my-service -o yaml | grep selector -A 5
# Compare selector labels with pod labels — they must match exactly

# Step 3: Check if pods are Ready
kubectl get pods
# Pods must be READY (readiness probe passing) to appear in endpoints

# Step 4: Test from inside the cluster
kubectl run test --image=busybox -it --rm -- wget -qO- http://my-service:80

# Step 5: Check port mapping
# Service port and targetPort must match the container's listening port
```

---

### Scenario 4: Rolling Update Stuck

**Q: You updated the image tag in your Deployment but the rollout is stuck at 1/3 replicas. Old pods are still running.**

```bash
kubectl rollout status deployment/web-app
# Shows: Waiting for deployment "web-app" rollout to finish: 1 out of 3 new replicas have been updated

kubectl get pods
# New pod is probably not Ready or CrashLoopBackOff

kubectl describe pod <new-pod>
kubectl logs <new-pod>
```

Root causes:
- New image doesn't exist (ImagePullBackOff)
- New version crashes on startup
- Readiness probe fails (new endpoint changed)
- Insufficient resources for new pod + `maxUnavailable: 0` means old pods won't be killed

Fix:
```bash
# Rollback
kubectl rollout undo deployment/web-app

# Fix the image/config, then redeploy
kubectl set image deployment/web-app web=myapp:2.1-fixed
```

---

### Scenario 5: Persistent Data Lost After Pod Restart

**Q: Your database pod restarted and all data is gone. What happened?**

A: The pod was using `emptyDir` or no volume at all. Container filesystems are ephemeral.

Fix: Use PersistentVolumeClaim:
```yaml
volumes:
  - name: data
    persistentVolumeClaim:
      claimName: db-data    # Survives pod restarts and rescheduling
```

Also check:
- Was `reclaimPolicy: Delete` set on the StorageClass? If the PVC was deleted, the PV and data are gone.
- Did someone run `kubectl delete pvc db-data`?
- Use `reclaimPolicy: Retain` for production data.

---

### Scenario 6: Node Failure

**Q: One of your 3 worker nodes goes down. What happens to the pods?**

A: 
1. Kubelet stops reporting to API server
2. After `node-monitor-grace-period` (default 40s), node is marked `NotReady`
3. After `pod-eviction-timeout` (default 5 minutes), pods are evicted
4. If pods are managed by a Deployment/ReplicaSet, scheduler creates new pods on healthy nodes
5. StatefulSet pods are NOT automatically rescheduled (to prevent data corruption) — manual intervention may be needed
6. DaemonSet pods are recreated when the node comes back

What should you have in place:
- PodDisruptionBudgets to ensure availability during eviction
- Pod anti-affinity to spread replicas across nodes
- Cluster Autoscaler to add nodes if needed
- Monitoring and alerting on node status

---

### Scenario 7: Secret Management in Production

**Q: How do you securely manage secrets in a production Kubernetes cluster?**

A:
1. **Never commit secrets to Git** — use External Secrets Operator to sync from AWS Secrets Manager/Vault
2. **Enable encryption at rest** for etcd
3. **Use RBAC** to restrict who can read secrets
4. **Use IRSA/Workload Identity** instead of storing cloud credentials

```yaml
# External Secrets Operator — syncs from AWS Secrets Manager
apiVersion: external-secrets.io/v1beta1
kind: ExternalSecret
metadata:
  name: db-credentials
spec:
  refreshInterval: 1h
  secretStoreRef:
    name: aws-secret-store
    kind: ClusterSecretStore
  target:
    name: db-secret
  data:
    - secretKey: password
      remoteRef:
        key: production/database
        property: password
```

---

### Scenario 8: Resource Limits and OOMKilled

**Q: Your Java app pod keeps getting OOMKilled. Memory limit is 512Mi. What do you do?**

```bash
# Confirm OOMKill
kubectl describe pod my-pod | grep -i oom
# Last State: Terminated, Reason: OOMKilled, Exit Code: 137

# Check actual memory usage
kubectl top pod my-pod
```

Fix:
1. Set JVM max heap lower than container limit (leave room for non-heap): `-Xmx384m` for a 512Mi container
2. Increase the container memory limit if the app genuinely needs more
3. Use VPA in "Off" mode to get recommendations without auto-applying
4. **Never set limits without setting requests** — requests are what the scheduler uses

```yaml
resources:
  requests:
    memory: "512Mi"     # Guaranteed memory
    cpu: "250m"
  limits:
    memory: "768Mi"     # Burst limit (OOMKilled if exceeded)
    cpu: "1"            # Throttled (not killed) if exceeded
```

---

### Scenario 9: Blue/Green Deployment

**Q: Implement a blue/green deployment strategy in Kubernetes.**

```yaml
# Blue deployment (current)
apiVersion: apps/v1
kind: Deployment
metadata:
  name: app-blue
spec:
  replicas: 3
  selector:
    matchLabels:
      app: myapp
      version: blue
  template:
    metadata:
      labels:
        app: myapp
        version: blue
    spec:
      containers:
        - name: app
          image: myapp:1.0

---
# Green deployment (new version)
apiVersion: apps/v1
kind: Deployment
metadata:
  name: app-green
spec:
  replicas: 3
  selector:
    matchLabels:
      app: myapp
      version: green
  template:
    metadata:
      labels:
        app: myapp
        version: green
    spec:
      containers:
        - name: app
          image: myapp:2.0

---
# Service — switch by changing selector
apiVersion: v1
kind: Service
metadata:
  name: myapp-service
spec:
  selector:
    app: myapp
    version: blue        # ← Change to "green" to switch
  ports:
    - port: 80
      targetPort: 8080
```

```bash
# Switch traffic to green
kubectl patch svc myapp-service -p '{"spec":{"selector":{"version":"green"}}}'

# Rollback — switch back to blue
kubectl patch svc myapp-service -p '{"spec":{"selector":{"version":"blue"}}}'

# Clean up old version after confirming green is stable
kubectl delete deployment app-blue
```

---

### Scenario 10: Cross-Namespace Communication

**Q: A pod in the `frontend` namespace needs to call a service in the `backend` namespace. How?**

A: Use the fully qualified DNS name:
```
<service-name>.<namespace>.svc.cluster.local
```

```yaml
# From frontend pod, call:
# http://api-service.backend.svc.cluster.local:80
env:
  - name: API_URL
    value: "http://api-service.backend.svc.cluster.local:80"
```

If NetworkPolicies are in place, you also need to allow cross-namespace traffic:
```yaml
ingress:
  - from:
      - namespaceSelector:
          matchLabels:
            kubernetes.io/metadata.name: frontend
```

---

### Scenario 11: Cluster Upgrade Strategy

**Q: How do you upgrade a Kubernetes cluster from 1.28 to 1.29 with minimal downtime?**

A:
1. **Read the changelog** for breaking changes and deprecated APIs
2. **Check API compatibility**: `kubectl convert` or `pluto detect-all-in-cluster`
3. **Upgrade control plane first** (API server, scheduler, controller-manager)
4. **Upgrade nodes one at a time** (rolling):
   ```bash
   kubectl cordon node1
   kubectl drain node1 --ignore-daemonsets --delete-emptydir-data
   # Upgrade node1 (OS, kubelet, etc.)
   kubectl uncordon node1
   # Repeat for each node
   ```
5. **PodDisruptionBudgets** ensure pods aren't all evicted at once
6. **Test in staging first**

On EKS:
```bash
eksctl upgrade cluster --name my-cluster --version 1.29 --approve
# Then upgrade each node group
eksctl upgrade nodegroup --cluster my-cluster --name workers
```

---

### Scenario 12: Debugging DNS Resolution

**Q: Your pod can't resolve service names. `curl api-service` returns "could not resolve host".**

```bash
# Step 1: Check CoreDNS is running
kubectl get pods -n kube-system -l k8s-app=kube-dns

# Step 2: Test DNS from inside a pod
kubectl run dnstest --image=busybox:1.36 -it --rm -- nslookup api-service
kubectl run dnstest --image=busybox:1.36 -it --rm -- nslookup api-service.default.svc.cluster.local

# Step 3: Check CoreDNS logs
kubectl logs -n kube-system -l k8s-app=kube-dns

# Step 4: Check the pod's DNS config
kubectl exec my-pod -- cat /etc/resolv.conf
# Should show: nameserver <CoreDNS ClusterIP>

# Step 5: Check if service exists
kubectl get svc api-service
```

Common causes:
- CoreDNS pods crashed or overloaded
- Pod's `dnsPolicy` is set to `None` or `Default` (uses node DNS, not cluster DNS)
- Service doesn't exist or is in a different namespace
- NetworkPolicy blocking UDP port 53

---

### Scenario 13: Cost Optimization

**Q: Your K8s cluster costs are too high. How do you optimize?**

1. **Right-size pods** — Use VPA recommendations to set accurate requests/limits
2. **Use Cluster Autoscaler** — scale nodes down when not needed
3. **Spot/Preemptible instances** for non-critical workloads
4. **Use Karpenter** (on AWS) for smarter node provisioning
5. **Namespace resource quotas** to prevent runaway usage
6. **Delete idle resources** — unused PVCs, old ReplicaSets, completed Jobs
7. **Use HPA** — scale down during low traffic
8. **Schedule non-critical jobs during off-peak** (CronJobs)
9. **Use Fargate** for bursty workloads (pay per pod, no idle nodes)

```bash
# Find pods requesting more than they use
kubectl top pods -A --sort-by=cpu
kubectl top pods -A --sort-by=memory
# Compare with resource requests in pod specs
```

---

### Scenario 14: Pod Security

**Q: How do you prevent a pod from running as root or escalating privileges?**

```yaml
spec:
  securityContext:
    runAsNonRoot: true
    runAsUser: 1000
    fsGroup: 2000
  containers:
    - name: app
      image: myapp:1.0
      securityContext:
        allowPrivilegeEscalation: false
        readOnlyRootFilesystem: true
        capabilities:
          drop:
            - ALL
```

Cluster-wide enforcement with Pod Security Standards:
```bash
# Label namespace to enforce restricted policy
kubectl label namespace production pod-security.kubernetes.io/enforce=restricted
kubectl label namespace production pod-security.kubernetes.io/warn=restricted
```

---

### Scenario 15: Multi-Cluster / Disaster Recovery

**Q: How do you design for disaster recovery across regions?**

A:
- **Active-Active**: Deploy to multiple clusters in different regions, Route53/Global Accelerator distributes traffic
- **Active-Passive**: Primary cluster handles traffic, secondary on standby
- **Velero** for cluster backup and restore:
  ```bash
  velero backup create full-backup --include-namespaces production
  velero restore create --from-backup full-backup
  ```
- **GitOps** (ArgoCD/Flux): Same manifests deployed to multiple clusters from Git — recovery is just pointing to a new cluster
- **Database replication** across regions (RDS Multi-AZ, Aurora Global Database)

---

### Quick-Fire Interview Questions

**Q: What's the difference between a Deployment and a StatefulSet?**
Deployments are for stateless apps — pods are interchangeable. StatefulSets give each pod a stable identity, persistent storage, and ordered startup/shutdown. Use StatefulSets for databases, message queues, clustered apps.

**Q: What happens when you `kubectl apply` vs `kubectl create`?**
`apply` is declarative — creates if not exists, updates if exists (idempotent). `create` is imperative — errors if resource already exists.

**Q: How does a Service route traffic to pods?**
Service uses a label selector to find matching pods. kube-proxy on each node maintains iptables/IPVS rules that route traffic from the Service ClusterIP to pod IPs.

**Q: What is a sidecar container?**
A helper container running alongside the main container in the same pod. Shares network and storage. Common uses: log shipping, service mesh proxy (Envoy/Istio), config sync.

**Q: Requests vs Limits?**
Requests = guaranteed minimum resources (used by scheduler for placement). Limits = maximum allowed. CPU over-limit = throttled. Memory over-limit = OOMKilled.

**Q: What is a headless service?**
A service with `clusterIP: None`. Instead of a single virtual IP, DNS returns individual pod IPs. Used with StatefulSets for direct pod addressing.

**Q: How do you debug a pod that won't start?**
`kubectl describe pod` → check Events. `kubectl logs` → check app output. `kubectl get events` → cluster-wide events. Common: image pull errors, resource constraints, probe failures, missing configmaps/secrets.

**Q: What is a PodDisruptionBudget?**
Ensures a minimum number of pods stay available during voluntary disruptions (node drain, cluster upgrade). Doesn't protect against involuntary disruptions (node crash).

**Q: Difference between liveness and readiness probes?**
Liveness failure → container is restarted. Readiness failure → pod is removed from service endpoints (no traffic routed to it) but not restarted. Both can coexist.

**Q: What is a DaemonSet used for?**
Runs exactly one pod per node. Used for node-level agents: log collectors (Fluentd), monitoring (Prometheus node exporter), network plugins (Calico), storage drivers (CSI).

**Q: How do you handle config changes that require pod restart?**
Kubernetes doesn't auto-restart pods on ConfigMap/Secret changes. Options: rolling restart (`kubectl rollout restart`), use a hash annotation that changes with config, or use Reloader controller.

**Q: What is Karpenter and how is it different from Cluster Autoscaler?**
Both scale nodes. Cluster Autoscaler works with predefined node groups (ASGs). Karpenter provisions nodes directly based on pod requirements — picks optimal instance types, zones, and purchase options (on-demand vs spot). Faster and more flexible.

---

*End of Kubernetes Complete Tutorial*
