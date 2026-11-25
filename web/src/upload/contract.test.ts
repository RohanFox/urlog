import { describe, expect, it } from 'vitest';
import type { UploadState } from '../api/schema.gen';
import contract from '../api/samples/upload-contract.json';
import { PENDING_TYPE, STATE_HEADER } from './protocol';

// The upload protocol is outside the description of the routes. This file of names is held to the backend by
// one of its tests and to the client by this one.
describe('the names of the upload protocol', () => {
  it('are the ones the backend writes', () => {
    expect(STATE_HEADER).toBe(contract.state_header);
    expect(contract.pending_problem_type.endsWith(`/${PENDING_TYPE}`)).toBe(true);
    expect(contract.pending_status).toBe(503);
    const states: UploadState[] = ['receiving', 'finalising', 'done', 'duplicate', 'rejected', 'failed'];
    expect(contract.states).toEqual(states);
  });
});
