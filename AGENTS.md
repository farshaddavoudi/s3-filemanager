# AGENTS.md – AI & Assistant Guidelines for `s3-filemanager`

This document explains how AI coding assistants (GitHub Copilot, ChatGPT, etc.) should work inside this repository.

The goal is to keep the project:
- Architecturally clean
- Extensible (backends, access policies, audit sinks)
- MinIO-first, but storage-agnostic
- Safe and easy to deploy via Docker

## 1. Project Summary

The project is a self-hosted web file manager for S3-compatible storage, beginning with MinIO.

### 1.1 What the app does

- Provides a web-based file explorer for S3-compatible storage.
- Uses path-based, prefix-oriented permissions.
- Supports Docker deployment.
- Supports OIDC or local authentication (configurable).

### 1.2 What the app is NOT

- Not a generic API gateway.
- Not a Keycloak admin tool.
- Not tied to any single company or internal system.

## 2. Core Concepts & Interfaces

AI agents must keep these interfaces clean and stable.

### 2.1 `IObjectStorageBackend`

Purpose: talk to physical storage (MinIO/S3 now, others later).

Responsible for:
- Listing items under a path.
- Uploading files.
- Deleting files and folders (prefix-based).
- Moving/renaming (copy + delete).
- Opening streams for download.

### 2.2 `IAccessPolicyProvider`

Purpose: decide what a user can do on a given path.

Responsible for:
- Returning effective permissions (read/write/delete/upload) for `(user, path)`.
- Combining user-level and role-level rules.
- Potentially reading from config / JSON / DB.

### 2.3 `IAuditSink`

Purpose: externalize audit logging.

Responsible for:
- Logging operations such as:
  - Read
  - Upload
  - Delete
  - Move/Rename

Default implementation can log to console; more advanced sinks can be added.

## 3. Layers & Responsibilities

AI agents must respect these architectural layers:

1. **UI / Frontend**
   - Renders the file manager.
   - Calls Web API endpoints.
   - Hides/disables actions based on permissions returned by the API.

2. **Web API**
   - Accepts HTTP requests for file operations.
   - Handles authentication (OIDC/local).
   - Queries `IAccessPolicyProvider` for permissions.
   - Calls `IObjectStorageBackend` for storage operations.
   - Logs via `IAuditSink`.

3. **Storage Backend Implementations**
   - Implement `IObjectStorageBackend`.
   - Encapsulate MinIO/S3 SDK usage and configuration.
   - Contain no access-policy logic.

4. **Configuration & Deployment**
   - Uses environment variables and `appsettings` for configuration.
   - Selects which backend implementation to use (e.g. `STORAGE__BACKEND=Minio`).

## 4. Coding Guidelines (for AI agents)

- Prefer clean, explicit code over clever tricks.
- Use dependency injection for all core services.
- Keep MinIO-specific logic in the MinIO backend project.
- Avoid static/global state.
- Keep code testable (small, focused classes).

### 4.1 Error Handling

- Use meaningful HTTP status codes (400/401/403/404/500).
- Do not leak internal exception details in responses in production builds.
- Log failures via `IAuditSink` or other logging mechanisms.

## 5. Virtual Agent Roles

To reason about work division, consider these roles:

- **Architect Agent** – defines interfaces and layering.
- **Storage Backend Agent** – implements MinIO/S3 details.
- **Access Policy Agent** – implements default `IAccessPolicyProvider`.
- **Auth & Security Agent** – sets up OIDC/local authentication.
- **DevOps Agent** – maintains Dockerfile, docker-compose, and deployment docs.

## 6. Questions Before Major Changes

Before making big changes, AI agents should consider:

1. Does this break public interfaces (`IObjectStorageBackend`, `IAccessPolicyProvider`, `IAuditSink`)?
2. Is this change MinIO-specific or generic?
3. Does this introduce new responsibilities into an already large class?
4. Can this be done in a backwards-compatible way?

## 7. Non-Goals

- Do not:
  - Add company-specific logic (e.g., ATA-specific services or `core-storage-api`) to this public repo.
  - Mix access-control logic into the storage backend implementations.
  - Hardcode secrets or license keys.
  - Overcomplicate the architecture with heavy frameworks or rule engines.

## 8. Summary for AI Assistants

If you are an AI assistant working in this repo, default to:

1. Respect the layered architecture: Web UI → Web API → Access Policies → Storage Backend.
2. Keep extensions pluggable using interfaces and DI.
3. Encapsulate vendor-specific details in their own backend implementations.
4. Think in terms of object-storage semantics (prefixes instead of real folders).
5. Produce clean, documented code suitable for open-source use.

## 9. Git Workflow & Commit/Branch Conventions

- Use **Conventional Commits**. Preferred types: `feat`, `fix`, `chore`, `docs`, `build`, `ci`, `refactor`, `test`. Subject in imperative, <= 72 chars; scope optional (e.g., `feat(web): add upload progress`).
- Branch naming: `feature/<scope>`, `fix/<scope>`, `chore/<scope>`, or `release/<version>`.
- Squash or rebase merges are fine; ensure final commit messages keep the Conventional Commit format.
