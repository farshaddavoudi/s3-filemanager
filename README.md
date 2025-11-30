<p align="center">
  <img src="docs/banner.svg" width="100%" />
</p>

<h1 align="center">S3 File Manager</h1>

<p align="center">
A modern, self-hosted web file manager for S3/MinIO — extensible backends, flexible access policies, and fully Docker-ready.
</p>

<p align="center">
    <a href="#features">Features</a> •
    <a href="#quickstart">Quickstart</a> •
    <a href="#configuration">Configuration</a> •
    <a href="#architecture">Architecture</a> •
    <a href="#roadmap">Roadmap</a> •
    <a href="#license">License</a>
</p>

---

## 📌 Overview

**S3 File Manager** is a self-hosted, extensible web application that provides a modern file-explorer experience on top of any **S3-compatible object storage**, including:

- MinIO  
- AWS S3  
- Ceph RGW  
- Wasabi, DigitalOcean Spaces, Backblaze B2  
- Any custom S3 gateway

It supports **multiple storage backends**, **configurable access policies**, and **clean Docker deployments**.

This project is designed to be:

- 🌍 **Tech-agnostic** — works in any stack  
- 🔌 **Extensible** — storage backend, policy provider, audit sink  
- 🛡️ **Secure** — integrates with SSO / OIDC or local auth  
- 🔧 **Configurable** — path-based permissions, virtual roots, etc.  
- 🐳 **Deployable** — single `docker run` or `docker compose up`

---

## ✨ Features

### Core
- 🗂 **Modern web file manager UI**
- 📁 Browse, upload, download, rename, delete, move
- 🔍 Search, sort, context menu, previews

### Storage Backends
- 🟦 **Built-in MinIO/S3 backend**
- 🔌 Custom backends via `IObjectStorageBackend`
- 🌐 Future support for filesystem / API proxy backends

### Access Control
- 🔑 Path-based permissions (read/write/delete/upload)
- 👥 Role-based or user-based policies  
- ⚙️ Pluggable policy engine with `IAccessPolicyProvider`

### Authentication
- 🧩 Supports:
  - OIDC / SSO (Keycloak, Auth0, Azure AD, Okta, etc.)
  - Local username/password (optional)
  - Anonymous mode (read-only)

### Extensibility
- 🧱 Storage backend abstraction
- 🧾 Custom audit sinks (`IAuditSink`)
- 📂 Configurable virtual root structure

### Deployment
- 🐳 Official Docker image  
- 🔧 Production-ready configuration  
- ☸️ Kubernetes manifests (coming soon)

---

## 🚀 Quickstart

### Run with Docker (basic MinIO setup)

```bash
docker run -d \
  -p 8080:8080 \
  -e STORAGE__BACKEND=Minio \
  -e MINIO__ENDPOINT=http://minio:9000 \
  -e MINIO__ACCESSKEY=minioadmin \
  -e MINIO__SECRETKEY=minioadmin \
  -e MINIO__BUCKET=ftp \
  farshaddavoudi/s3-filemanager:latest
