// Stable exit codes so shell automation can branch on failures.
export const EXIT = {
  OK: 0,
  USAGE: 1, // bad flags, missing required input, local validation failure
  AUTH: 2, // 401 -- the deployment rejected the API key
  NOT_FOUND: 3, // 404 -- no page matched the title or id
  NETWORK: 4, // DNS/TLS/timeout/connection refused
  BACKEND: 5, // 4xx/5xx the CLI cannot classify further
  CONFIG: 6, // no deployment URL or API key configured
  CONFLICT: 7, // 409 -- no default section, or an ambiguous page title
};

export class CliError extends Error {
  constructor(message, code = EXIT.USAGE, details = undefined) {
    super(message);
    this.code = code;
    this.details = details;
  }
}
