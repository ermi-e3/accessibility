# AccessLearn Ethiopia

AccessLearn Ethiopia is a Digital Bridge Track 2 showcase that connects an
accessible learning player to career opportunities and public certificate
verification. The repository includes the ASP.NET Core API, framework-neutral
Web Component player, Savanna static judge experience, and automated
accessibility coverage.

## Judge Demo

Requirements: Node.js 24.21, npm 11, and Chrome.

```powershell
npm install
npm run serve:fixtures
```

Open `http://127.0.0.1:4173/` and follow the three numbered modules:

1. Watch the captioned, transcript-enabled sample lesson.
2. Explore accessible job cards and spoken summaries.
3. Verify the learner's public certificate.

The bundled 25-second sample does not require Docker, PostgreSQL, object
storage, or the .NET API.

## Validation

```powershell
npm test
npm run test:e2e
npm run build
dotnet test accessibility.slnx
```

`npm test` validates the integration contracts and structured data. The
Playwright suite verifies the judge route, 200% zoom-equivalent reflow, speech
controls, axe results, player captions/transcript, completion events, and static
byte-range media delivery.

## Sample Media

Regenerate the production player bundle, rights-safe narration, sample WebM,
reviewed WebVTT track, transcript JSON, and machine caption draft with:

```powershell
npm run demo:prepare
```

Create an offline machine-generated draft VTT from another browser-decodable
video on Windows with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/extract-captions.ps1 `
  -InputVideo Savanna-static-pages/assets/media/accesslearn-demo/sample-lesson.webm `
  -OutputVtt Savanna-static-pages/assets/media/accesslearn-demo/new-caption-draft.vtt
```

Machine captions are drafts and must be reviewed before publication.

## Educator Accessibility Resources

The backend does not generate sign language. Educators upload a rights-cleared,
human-recorded EthSL interpretation and explicitly mark it reviewed:

```text
POST /api/v1/lessons/{lessonId}/assets
  ?role=signLanguage
  &language=eth
  &reviewed=true
  &accessibilityMode=human-recorded
```

Use `multipart/form-data` for the MP4 or WebM file. Publishing the lesson adds
the approved track to the player manifest; learners can then enable, move,
resize, minimize, maximize, or disable the interpretation window.

Educators can create or import Amharic transcript drafts through
`POST /api/v1/lessons/{lessonId}/transcripts` or
`POST /api/v1/lessons/{lessonId}/transcripts/import?language=am&format=WebVtt`,
then approve and publish them through the existing transcript workflow.

The judge fixture also exposes a downloadable ZIP containing local video,
English captions, an explicitly unreviewed Amharic draft, transcripts, and a
standalone `index.html` for offline learning.

## Repository Layout

```text
Savanna-static-pages/       Canonical judge showcase and sample media
src/accessibility.Api/      ASP.NET Core API and management endpoints
src/accessibility.Player/   Framework-neutral accessible player
src/accessibility.*/        Domain, application and infrastructure layers
tests/                      .NET, contract and browser tests
scripts/                    Demo server, media generation and import tools
sample-content/             Rights-safe sample narration source
docs/                       Architecture, build plan and upstream evidence
tools/                      Backend provisioning and player helpers
```

The Career Gateway listings and public credential are demonstration data.
Production deployment requires reviewed employer feeds and issuer-controlled
credential signing and revocation.
