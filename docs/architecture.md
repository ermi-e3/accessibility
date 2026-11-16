# Architecture

## Status

This documents the implemented Track 2 showcase and the team repository that
backs it. Production identity, storage and live employer services remain future
deployment concerns.

## Shape

Use one deployable web app and one deployable API. Keep the API as a
feature-based modular monolith. Reuse the same lesson data and API through the
web app and partner player instead of duplicating business logic.

```text
Angular web app           Partner host page
       |                         |
       |                 Web Component player
       |                         |
       +----------- REST API ----+
                         |
              ASP.NET Core modular monolith
                         |
              PostgreSQL + media storage
```

## Deployable units

### `Savanna-static-pages`

Owns page composition, navigation, user interaction, offline browser storage
and accessible presentation. It must not contain server credentials or become
the source of truth for business rules.

### `src/accessibility.Api`

Owns validation, permissions, lesson metadata, public certificate verification
and storage coordination. Features stay in one process and one database for
the hackathon.

### `src/accessibility.Player`

Owns the framework-neutral `<accesslearn-player>` integration surface. The
same built bundle is copied into the canonical static demo host. The distinct root
`src/accessibility.Player` tree is retained as an integration-contract
prototype, not a duplicate production package.

## Backend modules

- `Lessons`: lesson metadata, assets, captions and transcript access.
- `Jobs`: accessible career listings.
- `Certificates`: minimal public verification results.
- `OfflinePackages`: metadata and retrieval for lightweight lesson packages.

Infrastructure adapters belong under `Infrastructure`; feature rules do not
depend directly on PostgreSQL, the file system or a cloud provider.

## Frontend modules

- `learning`: lesson discovery and the accessible learning journey.
- `career`: job listings and application links.
- `certificates`: certificate verification UI.
- `offline`: download state and previously saved lessons.
- `core`: application-wide services and layout concerns.
- `shared`: reusable presentational components only.

## Dependency rules

1. Feature modules may use `core` and `shared`; they do not import one another's
   private internals.
2. The API is the source of truth for domain and authorization rules.
3. Database and storage code stay behind infrastructure boundaries.
4. The Web Component uses documented public API contracts only.
5. OpenAPI under `contracts/openapi` becomes the integration contract once generated.
6. Generated demo assets are labelled as samples; machine captions remain drafts until reviewed.

## Cross-cutting quality gates

- Semantic HTML and native controls first.
- Keyboard operation, visible focus and logical focus order.
- English and Amharic language metadata where applicable.
- Captions, transcript and human-produced EthSL demo media where required.
- Reflow at 200 percent zoom, with 400 percent retained as a production follow-up.
- Consistent API errors and no private data in public verification responses.
- Offline capability is accepted only after a disconnected-network test.

## Deliberately deferred

- LTI 1.3 and LTI Advantage
- Automatic EthSL generation
- Live or cloud transcription and translation pipelines
- Microservices, brokers, CQRS and MediatR
- Complex identity and partner OAuth until the official scope requires them
- Cloud deployment infrastructure

## Remaining production decisions

1. Confirm which certificate holder data may be public.
2. Select production hosting, storage and employer-feed providers.
3. Add issuer-controlled signing and revocation for credentials.
4. Commission reviewed Amharic, EthSL and audio-description media.
