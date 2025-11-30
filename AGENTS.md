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
- Web file explorer
- Object storage operations
- Path-based access control
- Docker deployment
- Optional OIDC/local authentication

### 1.2 What the app is NOT
- Not a generic API gateway
- Not a Keycloak admin tool
- Not tied to any single company

## 2. Core Concepts & Interfaces

### 2.1 IObjectStorageBackend
Handles storage operations.

### 2.2 IAccessPolicyProvider
Decides what users can do on paths.

### 2.3 IAuditSink
Logs audit events externally.

## 3. Layers & Responsibilities
- UI
- Web API
- Storage backend impl.
- Config/Deployment

## 4. Coding Guidelines
- Use DI
- Keep code clean/testable
- Keep MinIO logic isolated
- Do not add company-specific logic

## 5. Agent Roles
Architect, Storage Backend, Policy, Security, DevOps agents.

## 6. Questions Before Big Changes
- Will this break public interfaces?
- Is this backend-specific?
- Is this adding too many responsibilities?

## 7. Non-Goals
- No ATA-specific logic
- No mixing layers
- No static, untestable code

## 8. Summary
Use layered architecture, keep code testable, keep abstractions clean.
