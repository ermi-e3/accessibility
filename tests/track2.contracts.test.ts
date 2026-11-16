import { readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

const root = process.cwd();
const fixtureRoot = join(root, 'Savanna-static-pages');
const read = (...parts: string[]) => readFileSync(join(...parts), 'utf8');

function jsonLd(html: string, id: string): Record<string, unknown> {
  const match = html.match(new RegExp(`<script[^>]*id="${id}"[^>]*>([\\s\\S]*?)<\\/script>`));
  expect(match, `${id} JSON-LD script`).not.toBeNull();
  return JSON.parse(match![1]) as Record<string, unknown>;
}

describe('Track 2 integration contracts', () => {
  it('mounts the player directly after the lesson banner inside Savanna content', () => {
    const concept = read(fixtureRoot, 'pages', 'concept-with-navigation.html');
    const scope = concept.indexOf('<div class="savanna-styles">');
    const banner = concept.indexOf('VA-Content-Banner.png', scope);
    const player = concept.indexOf('<accesslearn-player', banner);
    const authoredFigure = concept.indexOf('<figure>', banner);
    expect(scope).toBeGreaterThanOrEqual(0);
    expect(banner).toBeGreaterThan(scope);
    expect(player).toBeGreaterThan(banner);
    expect(player).toBeLessThan(authoredFigure);
    expect(concept).toContain('href="jobs.html"');
  });

  it('defines and handles Digital Bridge progress and completion events', () => {
    const host = read(fixtureRoot, 'assets', 'js', 'accesslearn-savanna.js');
    const player = read(root, 'src', 'accessibility.Player', 'src', 'accesslearn-player.ts');
    const types = read(root, 'src', 'accessibility.Player', 'src', 'types.ts');
    for (const eventName of ['db-player:progress', 'db-player:complete']) {
      expect(host).toContain(`addEventListener('${eventName}'`);
      expect(player).toContain(`#emit("${eventName}"`);
      expect(types).toContain(`"${eventName}"`);
    }
  });

  it('publishes complete JobPosting structured data for every visible card', () => {
    const jobs = read(fixtureRoot, 'pages', 'jobs.html');
    const data = jsonLd(jobs, 'job-postings-data');
    const postings = data['@graph'] as Array<Record<string, unknown>>;
    expect(postings).toHaveLength(3);
    expect((jobs.match(/data-job-card/g) || [])).toHaveLength(postings.length);
    expect((jobs.match(/data-listen-summary/g) || [])).toHaveLength(postings.length);
    for (const posting of postings) {
      expect(posting['@type']).toBe('JobPosting');
      expect(posting).toMatchObject({
        title: expect.any(String),
        description: expect.any(String),
        datePosted: expect.any(String),
        validThrough: expect.any(String),
        employmentType: expect.any(String),
        hiringOrganization: expect.any(Object),
      });
    }
  });

  it('keeps speech optional and job cards reflowable at 200 percent zoom', () => {
    const script = read(fixtureRoot, 'assets', 'js', 'career-gateway.js');
    const styles = read(fixtureRoot, 'assets', 'css', 'career-gateway.css');
    expect(script).toContain("button.addEventListener('click'");
    expect(script).toContain('window.speechSynthesis.speak(utterance)');
    expect(script).toContain('The full text remains available.');
    expect(styles).toContain('minmax(min(100%, 20rem), 1fr)');
    expect(styles).toContain('overflow-wrap: anywhere');
    expect(styles).toContain('@media (max-width: 48rem)');
  });

  it('exposes a public Open Badges credential and employer verification link', () => {
    const certificate = read(fixtureRoot, 'pages', 'certificate.html');
    const credential = jsonLd(certificate, 'open-badge-credential-data');
    expect(credential['type']).toEqual(expect.arrayContaining(['VerifiableCredential', 'OpenBadgeCredential']));
    expect(credential['issuer']).toMatchObject({ name: 'AccessLearn Ethiopia' });
    expect(credential['credentialSubject']).toMatchObject({
      achievement: expect.objectContaining({ name: 'VA Foundations and Professional Identity' }),
    });
    expect(certificate).toContain('data-verification-link');
    expect(certificate).toContain('ALX-VA1-AMARA-2026');
  });

  it('ships a same-origin sample lesson with reviewed and extracted draft captions', () => {
    const manifest = JSON.parse(read(
      fixtureRoot,
      'api',
      'v1',
      'public',
      'lessons',
      'demo-accessible-careers',
      'manifest',
    )) as Record<string, any>;
    const reviewed = read(fixtureRoot, 'assets', 'media', 'accesslearn-demo', 'sample-captions.en.vtt');
    const draft = read(fixtureRoot, 'assets', 'media', 'accesslearn-demo', 'sample-captions.draft.vtt');
    const transcript = JSON.parse(read(
      fixtureRoot,
      'assets',
      'media',
      'accesslearn-demo',
      'sample-transcript.en.json',
    )) as Record<string, any>;
    expect(manifest).toMatchObject({
      schemaVersion: '1.0',
      lessonId: 'demo-accessible-careers',
      media: { contentType: 'video/webm', durationMilliseconds: expect.any(Number) },
      captions: [expect.objectContaining({ language: 'en', isHumanReviewed: true })],
    });
    expect((reviewed.match(/ --> /g) || [])).toHaveLength(4);
    expect(transcript.segments).toHaveLength(4);
    expect(draft).toContain('Machine-generated offline draft');
    expect(draft).toContain('inaccessible video player');
    expect(reviewed).toContain('an accessible video player');
    expect(statSync(join(fixtureRoot, 'assets', 'media', 'accesslearn-demo', 'sample-lesson.webm')).size).toBeGreaterThan(100_000);
    expect(statSync(join(fixtureRoot, 'assets', 'js', 'accesslearn-player.min.js')).size).toBeLessThan(100 * 1024);
    expect(read(fixtureRoot, 'pages', 'concept-with-navigation.html')).toContain('data-lesson-id="demo-accessible-careers"');
  });

  it('presents the player, jobs, and certificate as one judge route', () => {
    const showcase = read(fixtureRoot, 'index.html');
    expect(showcase).toContain('Start the 3-minute demo');
    expect(showcase).toContain('pages/concept-with-navigation.html#accessible-lesson-title');
    expect(showcase).toContain('pages/jobs.html');
    expect(showcase).toContain('pages/certificate.html?id=ALX-VA1-AMARA-2026');
    expect(showcase).toContain('sample-captions.draft.vtt');
    expect(showcase).toContain('sample-captions.en.vtt');
  });
});
