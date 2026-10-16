import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync, rmSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const requireFrontend = createRequire(join(root, 'src/frontend/autotable-src/package.json'));
const yaml = requireFrontend('js-yaml'); // Already supplied by the npm lockfile, not a new dependency.
const workflow = yaml.load(readFileSync(join(root, '.github/workflows/migration-build.yml'), 'utf8'));
const runner = 'tests/migration/run_source_ci.py';
const python = execFileSync('python3', ['-c', 'import sys; print(sys.executable)'], { encoding: 'utf8' }).trim();
const description = JSON.parse(execFileSync(python, [runner, '--describe'], {
  cwd: root, encoding: 'utf8', env: { ...process.env, PYTHONDONTWRITEBYTECODE: '1' },
}));
const groups = ['source-tools', 'backend', 'frontend-source', 'unity-source'];
const pin = 'd10d351e543aa3e341960de9800828b6c0662a82';
const plan = group => description.plans[group];
const named = (group, name) => plan(group).find(item => item.name === name);

function assertRegressionCommands(value) {
  assert.deepEqual(value.plans['frontend-source'].find(item => item.name === 'renderer-regressions'), {
    name: 'renderer-regressions',
    argv: [
      'node', '--test', '--test-reporter=tap',
      'tests/migration/renderer-backend-shape.test.mjs',
      'tests/migration/relay-authority-delta.test.mjs',
    ],
    cwd: '.', kind: 'node', expected: 6, dependency: false,
  });
  assert.deepEqual(value.plans['source-tools'].find(item => item.name === 'player-packaging'), {
    name: 'player-packaging',
    argv: ['python3', 'tests/migration/test_player_packaging.py', '-v'],
    cwd: '.', kind: 'unittest', expected: 1, dependency: false,
  });
  for (const path of [
    'tests/migration/renderer-backend-shape.test.mjs',
    'tests/migration/relay-authority-delta.test.mjs',
  ]) assert(!value.plans['frontend-source'].find(item => item.name === 'frontend-tests')
    .argv.some(argument => argument.endsWith(path)), `Keep ${path} separate from the frontend source cases`);
}

function assertSourceJobs(value) {
  assert.deepEqual(value.permissions, { contents: 'read' });
  assert.deepEqual(value.on.pull_request.branches, ['dev', 'main']);
  assert.deepEqual(value.on.push.branches, ['dev', 'main']);
  for (const group of groups) {
    const job = value.jobs[group];
    assert.equal(job['runs-on'], 'ubuntu-24.04', group);
    assert.equal(job.if, undefined, `mandatory ${group} cannot be conditionally skipped`);
    assert.equal(job['continue-on-error'], undefined, group);
    const runs = job.steps.filter(step => step.run);
    assert.deepEqual(runs.map(step => step.run), [`python3 ${runner} ${group} --install-dependencies`]);
    assert.equal(runs[0].if, undefined, group);
    assert.equal(runs[0]['continue-on-error'], undefined, group);
    assert.equal(job.steps.find(step => step.uses?.startsWith('actions/checkout@')).with['persist-credentials'], false);
    const upload = job.steps.find(step => step.uses?.startsWith('actions/upload-artifact@'));
    assert.equal(upload.if, 'always()', group);
    assert.equal(upload.with['if-no-files-found'], 'error', group);
    assert.deepEqual(upload.with.path.trim().split('\n'), description.evidencePaths[group], group);
  }
}

test('actual YAML has dev/main PR and push triggers, least privilege and immutable action references', () => {
  assertSourceJobs(workflow);
  for (const job of Object.values(workflow.jobs)) {
    for (const step of job.steps.filter(step => step.uses)) {
      assert.match(step.uses, /^actions\/[a-z-]+@[0-9a-f]{40}$/);
    }
  }
});

test('mandatory source jobs explicitly provision Node/.NET and execute without licensing/skip gates', () => {
  for (const group of groups) {
    const steps = workflow.jobs[group].steps;
    if (description.tools[group].includes('node')) {
      const setup = steps.find(step => step.uses?.startsWith('actions/setup-node@'));
      assert.equal(setup.with['node-version'], '20');
      assert.equal(setup.with['package-manager-cache'], false);
    }
    if (description.tools[group].includes('dotnet')) {
      const setup = steps.find(step => step.uses?.startsWith('actions/setup-dotnet@'));
      assert.equal(setup.with['dotnet-version'], '10.0.x');
      assert.equal(setup.with.cache, false);
    }
    assert.doesNotMatch(JSON.stringify(workflow.jobs[group]), /UNITY_LICENSE|UNITY_EDITOR|self-hosted/);
  }
  assert.deepEqual(description.tools['source-tools'], ['git', 'python3', 'bash', 'docker', 'pwsh', 'node']);
});

test('actual frontend command covers retained/Unity/production callers plus distinct strict typecheck and Vite', () => {
  assertRegressionCommands(description);
  const source = named('frontend-source', 'frontend-tests');
  assert.equal(source.cwd, 'src/frontend/autotable-src');
  assert.equal(source.expected, 103);
  assert.equal(named('frontend-source', 'workflow-contracts').expected, 12);
  for (const path of [
    'tests/node/room-startup-readiness.test.mjs',
    'tests/node/hand-result-render-and-wire.test.mjs',
    'tests/node/sc2-slot-owner.invariants.test.mjs',
    'tests/node/perspective-settings.test.mjs',
    'tests/node/unity-camera-controls.test.mjs',
    'tests/node/unity-pixel-evidence.test.mjs',
    '../../../tests/migration/browser-source-integration.test.mjs',
  ]) assert(source.argv.includes(path), path);
  assert(source.argv.some(path => path.startsWith('tests/node/unity-')));
  assert.deepEqual(named('frontend-source', 'dependencies').argv, ['npm', 'ci', '--no-audit', '--no-fund']);
  assert.deepEqual(named('frontend-source', 'typecheck').argv, ['npm', 'run', 'typecheck']);
  assert.deepEqual(named('frontend-source', 'vite-build').argv, ['npm', 'run', 'build']);
  assert.equal(JSON.parse(readFileSync(join(root, 'src/frontend/autotable-src/package.json'))).scripts.typecheck, 'tsc --noEmit');
  assert.equal(JSON.parse(readFileSync(join(root, 'src/frontend/autotable-src/tsconfig.json'))).compilerOptions.strict, true);
});

test('tools execute relocated cases, camera evidence, object/ignore/Docker gates and packaging using a public full pin', () => {
  assertRegressionCommands(description);
  const fetch = named('source-tools', 'baseline-fetch');
  assert.equal(description.baselineRevision, pin);
  assert.deepEqual(fetch.argv, [
    'git', '-c', 'credential.helper=', '-c', 'core.askPass=', '--git-dir', '{work}/baseline.git',
    'fetch', '--quiet', '--no-tags', '--depth=1', 'https://github.com/long2know/mahjong-autotable.git', pin,
  ]);
  assert(named('source-tools', 'baseline-init').argv.includes('--bare'));
  assert(named('source-tools', 'baseline-proof').argv.includes(pin + '^{commit}'));
  assert.deepEqual(named('source-tools', 'backend-tools').argv, ['python3', 'tests/migration/run_backend_tools.py']);
  assert.equal(named('source-tools', 'backend-tools').expected, 33);
  assert.deepEqual(named('source-tools', 'source-gates').argv, ['python3', 'tests/migration/test_source_gate.py', '-v']);
  assert.equal(named('source-tools', 'source-gates').expected, 9);
  assert.deepEqual(named('source-tools', 'player-evidence').argv, ['python3', 'tests/migration/test_browser_evidence.py', '-v']);
  assert.equal(named('source-tools', 'player-evidence').expected, 20);
  assert.equal(named('source-tools', 'missing-unity-artifact').kind, 'missing-unity');
  const implementation = readFileSync(join(root, runner), 'utf8');
  assert.match(implementation, /"MIGRATION_BASELINE_GIT_STORE": str\(self\.work \/ "baseline\.git"\)/);
  assert.match(implementation, /"GIT_TERMINAL_PROMPT": "0"/);
  assert.doesNotMatch(implementation, /session-state|\/data\/Copilot|sourcePins.*localPath/);
});

test('backend runs all 147 selected tests once with a fresh build and an actual Release publish', () => {
  const tests = plan('backend').filter(item => item.argv[0] === 'dotnet' && item.argv[1] === 'test');
  assert.equal(tests.length, 1);
  assert.equal(tests[0].expected, 147);
  assert(!tests[0].argv.includes('--no-build'));
  const filters = tests[0].argv[tests[0].argv.indexOf('--filter') + 1].split('|');
  assert.equal(filters.length, 14);
  for (const selector of [
    '.Autotable.ExistingRoomJoinTests', '.Autotable.AutotableWsRelayTests',
    '.Autotable.AutotableConfigJoinSemanticsTests', '.Autotable.HandResultAckWsTests',
    '.Autotable.AutotableHandshakeAndDealTriggerContractTests',
    '.Hosting.UnityStaticHostingTests', '.Observability.SecurityHeadersMiddlewareTests',
  ]) assert(filters.includes('FullyQualifiedName~' + selector), selector);
  const publish = named('backend', 'api-publish');
  assert.equal(publish.kind, 'publish');
  assert.equal(publish.argv[publish.argv.indexOf('-c') + 1], 'Release');
});

test('Unity source job executes metadata/frozen guards, five JS cases and actual 26-file CSharpSyntax producer', () => {
  assert.deepEqual(named('unity-source', 'asset-metadata').argv,
    ['python3', 'unity/MahjongRenderer/Tools/asset_metadata.py']);
  assert.deepEqual(named('unity-source', 'source-contract').argv, [
    'python3', 'unity/MahjongRenderer/Tools/check-source.py', '--frozen-contract', 'contracts/migration-contract.json',
  ]);
  assert.equal(named('unity-source', 'bridge-tests').expected, 5);
  assert(named('unity-source', 'syntax-build').argv.includes('unity/MahjongRenderer/Tools/CSharpSyntax/CSharpSyntax.csproj'));
  assert.deepEqual(named('unity-source', 'csharp-syntax').argv,
    ['dotnet', '{work}/syntax-bin/CSharpSyntax.dll', 'unity/MahjongRenderer']);
  assert.equal(named('unity-source', 'csharp-syntax').expected, 26);
  assert.doesNotMatch(JSON.stringify(plan('unity-source')), /unityci\/editor|build-unity|BuildPlayer|activate/);
  assert.match(readFileSync(join(root, 'unity/MahjongRenderer/Tools/CSharpSyntax/Program.cs'), 'utf8'), /LanguageVersion\.CSharp9/);
});

test('failure evidence uploads only named logs/receipts/TRX/source graph, never fixture stores or private input areas', () => {
  for (const [group, log] of [
    ['frontend-source', 'renderer-regressions.log'],
    ['source-tools', 'player-packaging.log'],
  ]) assert(description.evidencePaths[group].includes(`artifacts/test-results/migration-ci/${group}/${log}`));
  for (const group of groups) {
    for (const path of description.evidencePaths[group]) {
      assert.doesNotMatch(path, /\*|migration-work|baseline\.git|\.env|license|unity-builder-home|artifacts\/api|unity-webgl/);
      assert.match(path, /\.(json|log|trx)$/);
    }
  }
  assertSourceJobs(workflow);
});

test('real licensed job remains protected dispatch opt-in and depends on every mandatory source job', () => {
  const licensed = workflow.jobs['licensed-unity'];
  assert.equal(workflow.on.workflow_dispatch.inputs.build_unity.type, 'boolean');
  assert.equal(workflow.on.workflow_dispatch.inputs.build_unity.default, false);
  assert.equal(licensed.if, "github.event_name == 'workflow_dispatch' && inputs.build_unity");
  assert.equal(licensed.environment, 'licensed-unity-build');
  assert.deepEqual(licensed.needs, groups);
  assert.deepEqual(licensed['runs-on'], ['self-hosted', 'linux', 'x64', 'unity-6000.3.24f1']);
  assert(licensed.steps.some(step => step.run === 'bash build.sh --tag mahjong-unity:qualification'));
});

test('mutating actual YAML to conditionally skip a mandatory source guard is rejected', () => {
  for (const group of groups) {
    const changed = structuredClone(workflow);
    changed.jobs[group].if = "github.event_name == 'workflow_dispatch'";
    assert.throws(() => assertSourceJobs(changed), /cannot be conditionally skipped/);
  }
});

test('mutating actual workflow command/evidence or accepting command failure is rejected', () => {
  for (const mutate of [
    value => { value.jobs['frontend-source'].steps.find(step => step.run).run += ' || true'; },
    value => { value.jobs['source-tools'].steps.find(step => step.run)['continue-on-error'] = true; },
    value => { value.jobs['unity-source'].steps.find(step => step.uses?.startsWith('actions/upload-artifact@')).with.path = 'artifacts/**'; },
    value => {
      const upload = value.jobs['frontend-source'].steps.find(step => step.uses?.startsWith('actions/upload-artifact@'));
      upload.with.path = upload.with.path.replace(/.*\/renderer-regressions\.log\n/, '');
    },
    value => {
      const upload = value.jobs['source-tools'].steps.find(step => step.uses?.startsWith('actions/upload-artifact@'));
      upload.with.path = upload.with.path.replace(/.*\/player-packaging\.log\n/, '');
    },
  ]) {
    const changed = structuredClone(workflow);
    mutate(changed);
    assert.throws(() => assertSourceJobs(changed));
  }
  for (const [group, name] of [
    ['frontend-source', 'renderer-regressions'],
    ['source-tools', 'player-packaging'],
  ]) {
    for (const mutate of [
      value => { value.plans[group] = value.plans[group].filter(item => item.name !== name); },
      value => { value.plans[group].find(item => item.name === name).expected++; },
      value => { value.plans[group].find(item => item.name === name).dependency = true; },
      value => { value.plans[group].find(item => item.name === name).kind = null; },
      value => { value.plans[group].find(item => item.name === name).cwd = 'src/frontend/autotable-src'; },
    ]) {
      const changed = structuredClone(description);
      mutate(changed);
      assert.throws(() => assertRegressionCommands(changed));
    }
  }
});

test('actual runner fails missing prerequisites with persistent regression source hashes, not a skipped/pass status', t => {
  const fixture = join(root, 'artifacts/test-results/migration-work', 'ci-contract-' + randomUUID());
  mkdirSync(fixture, { recursive: true });
  t.after(() => rmSync(fixture, { recursive: true, force: true }));
  for (const [group, sources] of [
    ['source-tools', [
      'tests/migration/test_player_packaging.py',
      'src/frontend/autotable-src/src/renderer/renderer-contract.ts',
    ]],
    ['frontend-source', [
      'tests/migration/renderer-backend-shape.test.mjs',
      'tests/migration/relay-authority-delta.test.mjs',
      'src/frontend/autotable-src/tests/node/unity-test-support.mjs',
      'src/frontend/autotable-src/src/client.ts',
      'src/frontend/autotable-src/src/world.ts',
      'src/frontend/autotable-src/src/types.ts',
      'src/frontend/autotable-src/src/renderer/renderer-frame.ts',
      'src/frontend/autotable-src/src/renderer/renderer-sink.ts',
      'src/frontend/autotable-src/src/renderer/renderer-contract.ts',
    ]],
  ]) {
    const directory = join(fixture, group);
    const result = spawnSync(python, [runner, group, '--results-dir', relative(root, directory)], {
      cwd: root, encoding: 'utf8', env: { ...process.env, PATH: '', PYTHONDONTWRITEBYTECODE: '1' },
    });
    assert.equal(result.status, 1, result.stderr);
    const receipt = JSON.parse(readFileSync(join(directory, 'receipt.json')));
    assert.equal(receipt.status, 'failed');
    assert.equal(receipt.scope, 'SOURCE-ONLY');
    assert.equal(receipt.unityEditorApiShaderEditModeWebGlQualification, false);
    assert.match(receipt.error, /Required source-CI executable unavailable: git/);
    assert.deepEqual(receipt.commands, []);
    assert(receipt.sourceSha256[runner]);
    for (const path of sources) {
      assert.equal(receipt.sourceSha256[path], createHash('sha256').update(readFileSync(join(root, path))).digest('hex'), path);
    }
    assert(receipt.declaredEvidencePaths.includes(relative(root, join(directory, 'receipt.json'))));
    assert.match(readFileSync(join(directory, 'prerequisites.log'), 'utf8'), /unavailable: git/);
  }
});

test('actual immutable-object tests that skip for an absent override cannot pass the source-CI validator', () => {
  const result = spawnSync(python, ['-c', `
import importlib.util, json, os, subprocess, sys
spec = importlib.util.spec_from_file_location("source_ci", "${runner}")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
environment = dict(os.environ, MIGRATION_BASELINE_GIT_STORE="artifacts/test-results/migration-work/ABSENT-" + "${randomUUID()}", PYTHONDONTWRITEBYTECODE="1")
actual = subprocess.run([sys.executable, "tests/migration/test_source_gate.py",
    "ImportSourceTests.test_inventory_matches_actual_immutable_tree_and_allowlist_not_just_counts",
    "ImportSourceTests.test_actual_baseline_backend_cs_and_retained_notice_blobs_are_preserved",
    "-v"], text=True, capture_output=True, env=environment)
try:
    module.validate_output(dict(kind="unittest", expected=2), actual.stdout + actual.stderr)
except ValueError as error:
    print(json.dumps(dict(actualExitCode=actual.returncode, output=actual.stdout + actual.stderr, rejection=str(error))))
else:
    sys.exit("Incorrectly accepted actual skipped immutable-object tests")
`], { cwd: root, encoding: 'utf8', env: { ...process.env, PYTHONDONTWRITEBYTECODE: '1' } });
  assert.equal(result.status, 0, result.stderr);
  const evidence = JSON.parse(result.stdout);
  assert.equal(evidence.actualExitCode, 0);
  assert.match(evidence.output, /OK \(skipped=2\)/);
  assert.match(evidence.rejection, /Mandatory source tests skipped/);
});
