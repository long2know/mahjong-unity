import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
const root = process.cwd();
const require = createRequire(path.join(root, 'src/frontend/autotable-src/package.json'));
const yaml = require('js-yaml');
const workflow = yaml.load(fs.readFileSync('.github/workflows/migration-build.yml', 'utf8'));
const description = JSON.parse(execFileSync('python3', ['-B', 'tests/migration/run_source_ci.py', '--describe'], { encoding: 'utf8' }));

test('PR source workflow runs exact WebGL-only source groups without licensed/prebuilt engine gates', () => {
  assert.deepEqual(workflow.on.pull_request.branches, ['dev', 'main']);
  assert.deepEqual(workflow.on.push.branches, ['dev', 'main']);
  assert.deepEqual(workflow.permissions, { contents: 'read' });
  assert.equal(workflow.jobs['licensed-unity'], undefined);
  for (const group of Object.keys(description.plans)) {
    const job = workflow.jobs[group];
    assert(job);
    assert.equal(job.if, undefined);
    assert(job.steps.some(step => step.run === `python3 -B tests/migration/run_source_ci.py ${group} --install-dependencies`));
    const upload = job.steps.find(step => step.uses?.startsWith('actions/upload-artifact@'));
    assert.equal(upload.if, 'always()');
    assert.equal(upload.with['if-no-files-found'], 'error');
    assert.deepEqual(upload.with.path.trim().split('\n'), description.evidencePaths[group]);
  }
});

test('frontend inventory retains authority/input/layout/privacy callers and actual default graph checks', () => {
  const tests = description.plans['frontend-source'].find(item => item.name === 'frontend-tests');
  for (const name of ['renderer-actions', 'renderer-frame', 'renderer-camera-controls', 'webgl-style', 'webgl-build', 'webgl-renderer']) {
    assert(tests.argv.includes(`tests/node/${name}.test.mjs`));
  }
  assert.equal(new Set(tests.argv).size, tests.argv.length);
  assert.equal(tests.expected, 140);
  const commands = JSON.stringify(description.plans);
  assert.doesNotMatch(commands, /UNITY_LICENSE|unity-webgl|verify-unity|build_unity|baseline-fetch/);
});

test('hosted Docker smoke builds a real source image without publishing or touching production', () => {
  const job = workflow.jobs['image-runtime'];
  assert.equal(job['runs-on'], 'ubuntu-24.04');
  const commands = job.steps.map(step => step.run ?? '').join('\n');
  assert.match(commands, /docker build.*BUILD_SHA/);
  assert.match(commands, /container_smoke\.py/);
  assert.doesNotMatch(commands, /login|push|ghcr-production|imagetools create|5086/);
  assert.deepEqual(job.permissions ?? workflow.permissions, { contents: 'read' });
});

test('registry workflow identity/protected promotion and all actions remain pinned', () => {
  for (const name of ['ghcr-publish', 'ghcr-promote']) {
    const item = yaml.load(fs.readFileSync(`.github/workflows/${name}.yml`, 'utf8'));
    for (const job of Object.values(item.jobs)) {
      assert.match(job.if, /long2know\/mahjong-web/);
      assert.match(job.if, /refs\/heads\/main/);
      if (name === 'ghcr-promote') assert.equal(job.environment.name, 'ghcr-production');
    }
  }
  for (const job of Object.values(workflow.jobs)) for (const step of job.steps.filter(step => step.uses)) {
    assert.match(step.uses, /^[\w/-]+@[0-9a-f]{40}$/);
    if (step.uses.startsWith('actions/checkout@')) assert.equal(step.with['persist-credentials'], false);
  }
});
