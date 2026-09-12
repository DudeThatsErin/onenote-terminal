// A stand-in for a OneNote System deployment. It reproduces the parts of
// /api/health, /api/capture, and /api/append that the CLI depends on --
// including the exact status codes -- so the tests fail if either side drifts.

import { createServer } from 'node:http';

export const VALID_KEY = 'ons_test_key';
export const MAX_CONTENT_LENGTH = 100_000;

export async function startFakeBackend(options = {}) {
  const {
    healthy = true,
    hasDefaultSection = true,
    knownPages = ['Quick Inbox'],
    duplicateTitles = [],
    // Impersonate some other site at this address: 'html404' is what an
    // unrelated web app does with /api/health, and a string is a JSON service
    // that answers but identifies itself as something else.
    impersonate = null,
  } = options;

  const requests = [];

  const server = createServer(async (req, res) => {
    const body = await readJson(req);
    requests.push({ method: req.method, path: req.url, headers: req.headers, body });

    const send = (status, payload) => {
      res.writeHead(status, { 'content-type': 'application/json' });
      res.end(JSON.stringify(payload));
    };

    if (impersonate === 'html404') {
      res.writeHead(404, { 'content-type': 'text/html' });
      return res.end('<!DOCTYPE html><html><body>Not found</body></html>');
    }

    if (req.url === '/api/health') {
      if (typeof impersonate === 'string') return send(200, { ok: true, service: impersonate });
      return healthy
        ? send(200, { ok: true, service: 'onenote-system' })
        : send(503, { ok: false, error: 'DATABASE_URL is not configured.' });
    }

    // Every other route authenticates first, exactly as the deployment does.
    const match = /^Bearer\s+(\S+)\s*$/i.exec(req.headers.authorization || '');
    if (!match || match[1] !== VALID_KEY) {
      return send(401, { error: 'Use Authorization: Bearer YOUR_API_KEY.' });
    }

    if (req.url === '/api/capture') {
      if (!hasDefaultSection) return send(409, { error: 'This account has no default OneNote section yet.' });
      const title = typeof body?.title === 'string' && body.title.trim() ? body.title.trim().slice(0, 200) : 'Untitled capture';
      return send(201, {
        ok: true,
        page: { id: 'page-created', title, webUrl: 'https://onenote.example/page-created' },
      });
    }

    if (req.url === '/api/append') {
      const content = typeof body?.content === 'string' ? body.content : '';
      if (!content.trim()) return send(400, { error: 'content is required and cannot be empty.' });
      if (content.length > MAX_CONTENT_LENGTH) return send(400, { error: 'content is too long.' });

      const pageId = typeof body?.pageId === 'string' ? body.pageId.trim() : '';
      const pageTitle = typeof body?.pageTitle === 'string' ? body.pageTitle.trim() : '';
      if (!pageId && !pageTitle) return send(400, { error: 'Provide pageId or pageTitle.' });

      if (pageId) return send(200, { ok: true, page: { id: pageId, webUrl: `https://onenote.example/${pageId}` } });
      if (!hasDefaultSection) return send(409, { error: 'A default OneNote section is required when using a page title.' });
      if (duplicateTitles.includes(pageTitle)) {
        return send(409, { error: `More than one page is titled "${pageTitle}". Use a page ID to choose the exact page.` });
      }
      if (!knownPages.includes(pageTitle)) {
        return send(404, { error: `No page titled "${pageTitle}" was found in the default section.` });
      }
      return send(200, {
        ok: true,
        page: { id: 'page-existing', title: pageTitle, webUrl: 'https://onenote.example/page-existing' },
      });
    }

    send(404, { error: 'Not found' });
  });

  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address();

  return {
    url: `http://127.0.0.1:${port}`,
    requests,
    lastRequest: () => requests[requests.length - 1],
    close: () => new Promise((resolve) => server.close(resolve)),
  };
}

function readJson(req) {
  return new Promise((resolve) => {
    const chunks = [];
    req.on('data', (chunk) => chunks.push(chunk));
    req.on('end', () => {
      const text = Buffer.concat(chunks).toString('utf8');
      if (!text) return resolve(null);
      try {
        resolve(JSON.parse(text));
      } catch {
        resolve(null);
      }
    });
  });
}
