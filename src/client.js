// One HTTP client for every command: timeouts, JSON validation, the
// `Authorization: Bearer` header the deployment expects, and deployment errors
// mapped onto the CLI's exit codes.
//
// This is the same request shape the Apple Shortcut sends, so anything that
// works here works there and vice versa.

import { CliError, EXIT } from './exit.js';

const DEFAULT_TIMEOUT_MS = 15000;

export class Client {
  constructor({ url, apiKey, timeoutMs = DEFAULT_TIMEOUT_MS, fetchImpl = globalThis.fetch }) {
    this.url = url;
    this.apiKey = apiKey;
    this.timeoutMs = timeoutMs;
    this.fetchImpl = fetchImpl;
  }

  async request(method, path, body, { authenticated = true, tolerate = [] } = {}) {
    const target = `${this.url}/api${path}`;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), this.timeoutMs);

    let response;
    try {
      response = await this.fetchImpl(target, {
        method,
        headers: {
          ...(authenticated ? { authorization: `Bearer ${this.apiKey}` } : {}),
          accept: 'application/json',
          ...(body === undefined ? {} : { 'content-type': 'application/json' }),
        },
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: controller.signal,
      });
    } catch (err) {
      if (err && err.name === 'AbortError') {
        throw new CliError(`no response from ${this.url} after ${this.timeoutMs}ms`, EXIT.NETWORK);
      }
      // Never interpolate the key; only the URL and the transport reason.
      throw new CliError(`could not reach ${this.url}: ${reason(err)}`, EXIT.NETWORK);
    } finally {
      clearTimeout(timer);
    }

    const text = await response.text();
    let parsed = null;
    try {
      parsed = text ? JSON.parse(text) : null;
    } catch {
      parsed = null;
    }

    // Some responses carry their meaning in the body of a failure status --
    // /api/health answers 503 with the reason the database is unreachable, and
    // that reason is exactly what `doctor` exists to print.
    const tolerated = tolerate.includes(response.status) && parsed && typeof parsed === 'object';
    if (!response.ok && !tolerated) throw httpError(response.status, parsed, this.url);

    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      throw new CliError(`the deployment returned a non-JSON response (HTTP ${response.status})`, EXIT.BACKEND);
    }
    return parsed;
  }

  get(path, options) {
    return this.request('GET', path, undefined, options);
  }

  post(path, body, options) {
    return this.request('POST', path, body, options);
  }
}

// The deployment already writes plain-language errors ("No page titled ... was
// found"), so pass its message through and only choose the exit code here.
function httpError(status, parsed, url) {
  const message = parsed && typeof parsed.error === 'string' ? parsed.error : `HTTP ${status}`;
  if (status === 401) {
    return new CliError(`unauthorized: the deployment at ${url} rejected this API key`, EXIT.AUTH);
  }
  if (status === 404) return new CliError(message, EXIT.NOT_FOUND);
  if (status === 409) return new CliError(message, EXIT.CONFLICT);
  if (status === 400) return new CliError(message, EXIT.USAGE);
  return new CliError(message, EXIT.BACKEND);
}

function reason(err) {
  const code = err && (err.cause?.code || err.code);
  if (code === 'ENOTFOUND') return 'host not found';
  if (code === 'ECONNREFUSED') return 'connection refused';
  if (code === 'CERT_HAS_EXPIRED') return 'the TLS certificate has expired';
  if (code) return String(code);
  return err && err.message ? err.message : 'unknown error';
}
