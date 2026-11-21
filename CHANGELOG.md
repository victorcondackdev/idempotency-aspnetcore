# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-11-21

### Added

- `UseIdempotency()` middleware implementing Stripe-style idempotency keys: in-flight locking,
  `409 Conflict` with `Retry-After` for concurrent duplicates, `422 Unprocessable Entity` for a key
  reused with a different request, response replay with `Idempotent-Replayed: true`.
- `RequireIdempotency(required, timeToLive)` for minimal API endpoints and groups, and
  `[Idempotent]` for MVC controllers and actions.
- SHA-256 request fingerprint over method, path and body, optionally query string and selected
  headers.
- Keys scoped by a configurable `ScopeResolver` (tenant or user), HTTP method and route.
- `5xx`, `408` and `429` responses and unhandled exceptions release the key so clients can retry.
- Write-through response capture with a size limit; oversized or streamed responses are marked as
  not replayable instead of being executed twice.
- `IIdempotencyStore` abstraction with `InMemoryIdempotencyStore` and
  `DistributedCacheIdempotencyStore`; expiry driven by `TimeProvider`.
- RFC 9457 problem details with a stable `code` extension for every rejection.

[Unreleased]: https://github.com/victorcondackdev/idempotency-aspnetcore/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/victorcondackdev/idempotency-aspnetcore/releases/tag/v0.1.0
