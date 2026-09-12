// Due dates are validated locally so a typo costs a round trip to nobody, and
// so the shorthand a person actually types in a shell works.

import { CliError, EXIT } from './exit.js';

const RELATIVE = /^\+(\d+)\s*(d|days?|w|weeks?)$/i;
const DAY_WORDS = { today: 0, tomorrow: 1, yesterday: -1 };

function pad(n) {
  return String(n).padStart(2, '0');
}

function localDate(date) {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/**
 * Returns either a plain YYYY-MM-DD or a local ISO date-time without a zone.
 * Microsoft To Do pairs the value with an explicit time zone, so a bare date
 * must stay a bare date -- turning it into UTC here would shift the task a day
 * for anyone west of London.
 */
export function parseDueDate(input, flag, now = new Date()) {
  const value = String(input || '').trim();
  if (!value) throw new CliError(`${flag} is required`, EXIT.USAGE);

  const relative = RELATIVE.exec(value);
  if (relative) {
    const days = Number(relative[1]) * (/^w/i.test(relative[2]) ? 7 : 1);
    const date = new Date(now);
    date.setDate(date.getDate() + days);
    return localDate(date);
  }

  const lower = value.toLowerCase();
  if (lower in DAY_WORDS) {
    const date = new Date(now);
    date.setDate(date.getDate() + DAY_WORDS[lower]);
    return localDate(date);
  }

  if (/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    const [y, m, d] = value.split('-').map(Number);
    const check = new Date(y, m - 1, d);
    if (check.getFullYear() !== y || check.getMonth() !== m - 1 || check.getDate() !== d) {
      throw new CliError(`${flag} "${value}" is not a real date`, EXIT.USAGE);
    }
    return value;
  }

  const dateTime = /^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})(?::(\d{2}))?$/.exec(value);
  if (dateTime) {
    const [, y, mo, d, h, mi, s] = dateTime;
    return `${y}-${mo}-${d}T${h}:${mi}:${s || '00'}`;
  }

  throw new CliError(
    `${flag} "${value}" is not a date. Try 2026-09-15, "tomorrow", or "+3d".`,
    EXIT.USAGE
  );
}

/** The caller's IANA zone, so the deployment can anchor a bare date correctly. */
export function localTimeZone() {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

export function formatDue(value) {
  if (!value) return '';
  // A bare date has no time to render; anything else is a real moment.
  if (/^\d{4}-\d{2}-\d{2}$/.test(value)) return value;
  const date = new Date(/Z$|[+-]\d{2}:\d{2}$/.test(value) ? value : `${value}Z`);
  return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleString();
}
