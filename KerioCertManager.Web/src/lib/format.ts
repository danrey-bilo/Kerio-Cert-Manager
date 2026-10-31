import { t } from '../lib/i18n';
import { intlLocale } from './i18n';
import type { PasswordPolicy } from './types';

const DATE_TIME = new Intl.DateTimeFormat(intlLocale(), { dateStyle: 'short', timeStyle: 'short' });
const DATE = new Intl.DateTimeFormat(intlLocale(), { dateStyle: 'short' });

export const fmtDateTime = (iso: string | null | undefined) => (iso ? DATE_TIME.format(new Date(iso)) : '—');

export const fmtDate = (ymd: string | null | undefined) => (ymd ? DATE.format(new Date(ymd + 'T00:00:00')) : '—');

export function passwordRules(p: PasswordPolicy): string {
  const parts = [t('от {0} символов', p.min_length)];
  if (p.require_uppercase) parts.push(t('заглавная'));
  if (p.require_lowercase) parts.push(t('строчная'));
  if (p.require_digit) parts.push(t('цифра'));
  if (p.require_symbol) parts.push(t('спецсимвол'));
  return parts.join(' · ');
}

/** Браузер и система словами: «Chrome · Windows». */
export function agentText(agent: string | null): string {
  if (!agent) return t('Неизвестное устройство');
  const browser = /Edg\//.test(agent) ? 'Edge' : /Firefox\//.test(agent) ? 'Firefox' : /Chrome\//.test(agent) ? 'Chrome'
    : /Safari\//.test(agent) ? 'Safari' : /curl/i.test(agent) ? 'curl' : t('Браузер');
  const os = /Windows/.test(agent) ? 'Windows' : /Mac OS/.test(agent) ? 'macOS' : /Android/.test(agent) ? 'Android'
    : /iPhone|iPad/.test(agent) ? 'iOS' : /Linux/.test(agent) ? 'Linux' : '';
  return os ? browser + ' · ' + os : browser;
}
