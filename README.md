
<p align="center">
  <img src="docs/banner.svg" width="100%" />
</p>

<h1 align="center">S3 File Manager</h1>

<p align="center">
A modern, self-hosted web file manager for S3/MinIO — extensible backends, flexible access policies, and fully Docker-ready.
</p>

<p align="center">
    <a href="#features">Features</a> •
    <a href="#current-status">Current Status</a> •
    <a href="#quickstart">Quickstart</a> •
    <a href="#configuration">Configuration</a> •
    <a href="#architecture">Architecture</a> •
    <a href="#roadmap">Roadmap</a> •
    <a href="#license">License</a>
</p>

---

## 📌 Overview

**S3 File Manager** is a self-hosted, extensible web application that delivers a modern file-explorer experience on top of any **S3-compatible object storage**, starting with native support for **MinIO**.

It is designed with strong architectural boundaries:

- 🔌 **Pluggable storage backends** (`IObjectStorageBackend`)
- 🔐 **Customizable access policies** (`IAccessPolicyProvider`)
- 🧾 **Pluggable audit logging** (`IAuditSink`)
- 🐳 **Fully Docker-ready**

While MinIO is the first supported backend, the architecture is cloud-agnostic and intentionally built to support multiple object-storage providers in the future.

---

## 🟢 Current Status

### **Supported Now**
- ✔️ MinIO / S3-compatible storage  
- ✔️ Docker deployment  
- ✔️ File operations (browse, upload, download, rename, delete, move)  
- ✔️ Path-based access policies  
- ✔️ Pluggable policy provider (`IAccessPolicyProvider`)  
- ✔️ Pluggable storage backend interface  
- ✔️ Basic authentication modes (OIDC/local)

### **Planned**
- ⏳ Azure Blob Storage backend  
- ⏳ AWS S3 / Ceph RGW / Wasabi / Backblaze B2 backends  
- ⏳ Multiple virtual roots  
- ⏳ File previews and thumbnails  
- ⏳ Link sharing (pre-signed URLs)  
- ⏳ Admin configuration dashboard  
- ⏳ Kubernetes Helm chart  
- ⏳ Localization  
- ⏳ Advanced audit sinks (DB, MQ, webhooks)

---

## ✨ Features

### Core
- 🗂 Modern web file manager UI  
- 📁 Browse, upload, download, rename, delete, move  
- 🔍 Search, sort, and right-click menus  

### Storage Backends
- 🟦 Built-in MinIO backend  
- 🔌 Custom backends via `IObjectStorageBackend`  
- 🌐 Designed for future Azure Blob / AWS S3 support

### Access Control
- 🔑 Path-based permissions  
- 👥 User & role mapping  
- 🧩 Policy engine with `IAccessPolicyProvider`

### Authentication
- 🧱 OIDC/SSO integration (Keycloak, Auth0, Azure AD...)  
- 🔐 Local user mode (optional)  
- 👁 Public read-only mode  

### Extensibility
- 🧱 Backend abstraction  
- 🧾 Custom audit sinks (`IAuditSink`)  
- 📂 Configurable virtual folder structure  

### Deployment
- 🐳 Official Docker image  
- 🔧 Env-based configuration  
- ☸️ Kubernetes support (planned)

---

## 🚀 Quickstart

```bash
docker run -d \
  -p 8080:8080 \
  -e STORAGE__BACKEND=Minio \
  -e MINIO__ENDPOINT=http://minio:9000 \
  -e MINIO__ACCESSKEY=minioadmin \
  -e MINIO__SECRETKEY=minioadmin \
  -e MINIO__BUCKET=ftp \
  farshaddavoudi/s3-filemanager:latest
```

---

## ⚙️ Configuration

Example environment variables:

```bash
STORAGE__BACKEND=Minio
MINIO__ENDPOINT=https://minio.example.com
MINIO__BUCKET=ftp-data
AUTH__MODE=Oidc
AUTH__OIDC__AUTHORITY=https://sso.example.com/realms/main
```

---

## 🏛 Architecture

```
+---------------------------+
|        Web UI (JS)        |
+------------+--------------+
             |
             v
+---------------------------+
|         Web API           |
|  - File operations        |
|  - Auth (OIDC/local)      |
|  - Access policies        |
|  - Audit logging          |
+------------+--------------+
             |
             v
+---------------------------+
|   IObjectStorageBackend   |
+------------+--------------+
             |
             v
 +--------------------------+
 | MinIO / Azure Blob / ...|
 +--------------------------+
```

---

## 🧩 Extension Points

### `IObjectStorageBackend`
Handles listing, uploading, deleting, moving, downloading.

### `IAccessPolicyProvider`
Evaluates user/role permissions for a given path.

### `IAuditSink`
Optional external audit logging pipeline.

---

## 🛣 Roadmap

- [ ] Azure Blob backend  
- [ ] AWS S3/Ceph/Wasabi/Backblaze backends  
- [ ] Thumbnails & previews  
- [ ] Shareable links  
- [ ] Admin dashboard  
- [ ] OIDC claim mapping  
- [ ] Helm chart  
- [ ] REST API client  

---

## 🤝 Contributing

Issues and PRs are welcome.  
To create a custom backend, implement `IObjectStorageBackend` and submit a PR.

---

## 📄 License

MIT License — free for commercial and organizational use.
