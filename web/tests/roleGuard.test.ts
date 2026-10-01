// Run with `npm test` (node --test, no extra dependencies). Covers the shared role guard behind
// RoleRequiredAlert; the backend's own authorization and evidence rules are tested in AiReap.Tests.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { roleGuard, roleRequiredMessage } from '../src/lib/roleGuard.ts';

test('the required role may act', () => {
  assert.deepEqual(roleGuard('Developer', ['Developer']), { kind: 'allowed' });
  assert.deepEqual(roleGuard('QA', ['QA']), { kind: 'allowed' });
  assert.deepEqual(roleGuard('BusinessAnalyst', ['BusinessAnalyst']), { kind: 'allowed' });
  assert.deepEqual(roleGuard('Reviewer', ['Reviewer']), { kind: 'allowed' });
});

test('a user holding the required role alongside Administrator acts under the required role', () => {
  // Mirrors ArtifactWorkService / ProjectLifecyclePolicy: the owner role is preferred over Administrator.
  assert.deepEqual(roleGuard('Developer', ['Administrator', 'Developer']), { kind: 'allowed' });
});

test('a wrong role is blocked', () => {
  assert.deepEqual(roleGuard('QA', ['Developer']), { kind: 'blocked' });
  assert.deepEqual(roleGuard('Developer', ['BusinessAnalyst', 'Reviewer']), { kind: 'blocked' });
  assert.deepEqual(roleGuard('Reviewer', []), { kind: 'blocked' });
});

test('an Administrator is never treated as the required role', () => {
  // Administrator records are not Developer/QA evidence; the guard keeps them apart.
  assert.deepEqual(roleGuard('Developer', ['Administrator']), { kind: 'administrator' });
  assert.deepEqual(roleGuard('QA', ['Administrator']), { kind: 'administrator' });
  assert.notDeepEqual(roleGuard('Reviewer', ['Administrator']), { kind: 'allowed' });
});

test('Administrator trying a Developer action gets the Developer message', () => {
  assert.deepEqual(roleRequiredMessage('Developer', ['Administrator']), {
    title: 'Developer role required',
    body: 'This action must be performed by a Developer. You are currently signed in as Administrator. Please switch to a Developer account to continue.',
  });
});

test('Developer trying a QA action gets the QA message', () => {
  assert.deepEqual(roleRequiredMessage('QA', ['Developer']), {
    title: 'QA role required',
    body: 'This action must be performed by a QA user. You are currently signed in as Developer. Please switch to a QA account to continue.',
  });
});

test('Business Analyst and Reviewer messages follow the same pattern', () => {
  assert.deepEqual(roleRequiredMessage('BusinessAnalyst', ['QA']), {
    title: 'Business Analyst role required',
    body: 'This action must be performed by a Business Analyst. You are currently signed in as QA. Please switch to a Business Analyst account to continue.',
  });
  assert.deepEqual(roleRequiredMessage('Reviewer', ['Developer', 'QA']), {
    title: 'Reviewer role required',
    body: 'This action must be performed by a Reviewer. You are currently signed in as Developer and QA. Please switch to a Reviewer account to continue.',
  });
});

test('a user without roles is told so', () => {
  assert.match(roleRequiredMessage('Developer', []).body, /signed in without a project role/);
});
