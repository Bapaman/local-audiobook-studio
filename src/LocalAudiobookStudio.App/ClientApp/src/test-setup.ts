import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach, beforeEach, vi } from 'vitest';
import { TestEventSource } from './test-events';

beforeEach(() => { localStorage.clear(); TestEventSource.instances = []; vi.stubGlobal('EventSource', TestEventSource); });

afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
