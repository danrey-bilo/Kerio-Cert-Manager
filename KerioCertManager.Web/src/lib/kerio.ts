import { t } from '../lib/i18n';
import type { DotKind } from '../ui/bits';
import type { CertState, Deployment, KerioServer, Origin, Outcome, ServerStatus, StoreCert } from './types';

/** Статус сервера: точка, короткое слово, важность для сортировки (меньше — важнее). */
export const STATUS: Record<ServerStatus, { dot: DotKind; label: string; rank: number; tone: string }> = {
  expired: { dot: 'err', label: t('Истёк'), rank: 0, tone: 'err' },
  error: { dot: 'err', label: t('Ошибка'), rank: 1, tone: 'err' },
  deploy_failed: { dot: 'err', label: t('Не установлен'), rank: 2, tone: 'err' },
  name_stale: { dot: 'err', label: t('Подаётся прежний'), rank: 2, tone: 'err' },
  update_pending: { dot: 'warn', label: t('Ждёт установки'), rank: 3, tone: 'warn' },
  expiring: { dot: 'warn', label: t('Скоро истекает'), rank: 4, tone: 'warn' },
  unchecked: { dot: '', label: t('Проверяется'), rank: 5, tone: '' },
  unbound: { dot: '', label: t('Наблюдение'), rank: 6, tone: '' },
  ok: { dot: 'ok', label: t('В порядке'), rank: 7, tone: 'ok' },
  disabled: { dot: '', label: t('Отключён'), rank: 8, tone: '' },
};

export const OUTCOME: Record<Outcome, { dot: DotKind; label: string }> = {
  running: { dot: 'warn', label: t('Выполняется') },
  succeeded: { dot: 'ok', label: t('Установлен') },
  failed: { dot: 'err', label: t('Ошибка') },
  rolled_back: { dot: 'warn', label: t('Возвращён прежний') },
  dry_run: { dot: '', label: t('Пробный прогон') },
};

export const TRIGGER: Record<string, string> = {
  renewal: t('Продление в KMP'),
  expiring: t('Истекал на сервере'),
  initial: t('Первая установка'),
  manual: t('Вручную'),
  retry: t('Повтор'),
};

export const STEP: Record<string, string> = {
  fetch: t('Выгрузка из KMP'),
  validate: t('Проверка пары'),
  login: t('Вход в Kerio'),
  inventory: t('Хранилище Kerio'),
  dry_run: t('Пробный прогон'),
  upload_key: t('Загрузка ключа'),
  import_key: t('Импорт ключа'),
  upload_certificate: t('Загрузка сертификата'),
  import_certificate: t('Импорт сертификата'),
  locate: t('Поиск в хранилище'),
  activate: t('Активация'),
  verify: t('Проверка по TLS'),
  rollback: t('Откат'),
  restore_default: t('Сертификат по умолчанию'),
  cleanup: t('Уборка'),
  done: t('Готово'),
};

/** Отпечаток парами через двоеточие. */
export const fingerprint = (hex: string | null | undefined) => (hex ? hex.match(/.{1,2}/g)!.join(':') : '—');

/** Короткий отпечаток: первые и последние байты. */
export const shortFingerprint = (hex: string | null | undefined) =>
  hex ? fingerprint(hex.slice(0, 8)) + ' … ' + fingerprint(hex.slice(-8)) : '—';

/** Хост и порт из адреса. */
export const hostOf = (url: string) => url.replace(/^https?:\/\//, '').replace(/\/$/, '');

/** Сколько дней до даты (ISO); null — даты нет. */
export function daysUntil(iso: string | null | undefined): number | null {
  if (!iso) return null;
  return Math.floor((new Date(iso).getTime() - Date.now()) / 86_400_000);
}

/** «через 12 дн.», «истёк 3 дн. назад», «сегодня». */
export function daysText(days: number | null): string {
  if (days === null) return '—';
  if (days < 0) return t('истёк {0} дн. назад', Math.abs(days));
  if (days === 0) return t('истекает сегодня');
  return t('{0} дн.', days);
}

/** Цвет срока: меньше недели — красный, меньше порога — оранжевый. */
export function daysTone(days: number | null, warning: number): '' | 'ok' | 'warn' | 'err' {
  if (days === null) return '';
  if (days < 7) return 'err';
  if (days <= warning) return 'warn';
  return 'ok';
}

/** Сколько длилась установка. */
export function duration(d: Deployment): string {
  if (!d.finished_at) return '';
  const seconds = Math.max(0, Math.round((new Date(d.finished_at).getTime() - new Date(d.started_at).getTime()) / 1000));
  return seconds < 60 ? t('{0} с', seconds) : t('{0} мин {1} с', Math.floor(seconds / 60), (seconds % 60));
}

/** «5 мин назад». */
export function ago(iso: string | null | undefined): string {
  if (!iso) return t('никогда');
  const seconds = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 45) return t('только что');
  if (seconds < 3600) return t('{0} мин назад', Math.round(seconds / 60));
  if (seconds < 86_400) return t('{0} ч назад', Math.round(seconds / 3600));
  return t('{0} дн. назад', Math.round(seconds / 86_400));
}

/** Адреса из строки через запятую, пробел или перевод строки. */
export const splitList = (text: string) => text.split(/[\s,;]+/).map(x => x.trim()).filter(Boolean);

export type Connection = 'ok' | 'checking' | 'nopass' | 'auth' | 'forbidden' | 'down' | 'off';

/** Связь с сервером одним словом: только «можем ли мы до него достучаться и войти». */
export function connection(s: KerioServer): { kind: Connection; dot: DotKind; label: string; text: string } {
  if (!s.enabled) return { kind: 'off', dot: '', label: t('Отключён'), text: t('Сервис не проверяет и не обновляет этот сервер.') };
  if (!s.password_set) return { kind: 'nopass', dot: 'err', label: t('Пароль не задан'), text: t('Задайте пароль учётной записи Kerio.') };
  if (s.auth_rejected) {
    return { kind: 'auth', dot: 'err', label: t('Пароль отвергнут'), text: s.last_check_error ?? t('Kerio не принял логин или пароль.') };
  }
  if (!s.last_check_at) return { kind: 'checking', dot: '', label: t('Проверяется'), text: t('Первая проверка — в ближайшем проходе.') };
  if (s.kerio_role && s.kerio_role !== 'FullAdmin') {
    return { kind: 'forbidden', dot: 'err', label: t('Нет прав'), text: t('Роль учётной записи — {0}, нужна FullAdmin.', s.kerio_role) };
  }
  if (s.last_check_error) return { kind: 'down', dot: 'err', label: t('Нет связи'), text: s.last_check_error };
  return {
    kind: 'ok',
    dot: 'ok',
    label: t('Подключён'),
    text: [s.kerio_version && 'Kerio Connect ' + s.kerio_version, s.username, s.kerio_role].filter(Boolean).join(' · '),
  };
}

export const ORIGIN: Record<Origin, { label: string; tone: string; hint: string }> = {
  managed: { label: t('Из KMP · ведёт Kerio Cert Manager'), tone: 'ok', hint: t('Привязанный сертификат Key Manager Plus: продления ставятся сами') },
  kmp: { label: t('Есть в KMP'), tone: 'info', hint: t('Выпуск из Key Manager Plus, но сервер к нему не привязан') },
  foreign: { label: t('Не из KMP'), tone: 'warn', hint: t('Положен в Kerio вручную, мимо Key Manager Plus и Kerio Cert Manager') },
  builtin: { label: t('Встроенный Kerio'), tone: '', hint: t('Самоподписанный сертификат самого Kerio — на него Kerio падает без своего') },
  request: { label: t('Запрос'), tone: '', hint: t('Запрос на сертификат в Kerio — ещё не сертификат') },
};

export const CERT_STATE: Record<CertState, { dot: DotKind; label: string }> = {
  expired: { dot: 'err', label: t('Истёк') },
  expiring: { dot: 'warn', label: t('Истекает') },
  ok: { dot: 'ok', label: t('В порядке') },
  unknown: { dot: '', label: t('Срок неизвестен') },
};

/** Считается в сводке обзора: заменённые новой версией — нет; встроенный Kerio и запросы — только если подаются. */
export const counted = (c: StoreCert) => !c.superseded && (c.served || (c.origin !== 'builtin' && c.origin !== 'request'));

export const EVENT: Record<string, string> = {
  expiring: t('Скоро истекает'),
  expired: t('Истёк'),
  overdue: t('Не обновился сам'),
  renewal_pending: t('Ждёт ручной установки'),
  deployed: t('Установлен'),
  deploy_failed: t('Не установлен'),
  rolled_back: t('Возвращён прежний'),
  queue_summary: t('Сводка очереди'),
  unreachable: t('Недоступен'),
  auth_rejected: t('Пароль отвергнут'),
  not_admin: t('Нет прав'),
  not_trusted: t('Не доверен'),
  drift: t('Сменили вручную'),
  source_missing: t('Нет в KMP'),
  test: t('Проверка'),
};

/** Имя, которым сервис спрашивает сертификат по SNI (как на сервере): подстановка — придуманным первым уровнем. */
export function sniName(commonName: string): string {
  const name = commonName.trim().replace(/\.$/, '').toLowerCase();
  return name.startsWith('*.') ? 'kcm-check' + name.slice(1) : name;
}

function levenshtein(a: string, b: string): number {
  const row = Array.from({ length: b.length + 1 }, (_, j) => j);
  for (let i = 1; i <= a.length; i++) {
    let corner = row[0]!;
    row[0] = i;
    for (let j = 1; j <= b.length; j++) {
      const up = row[j]!;
      row[j] = Math.min(row[j]! + 1, row[j - 1]! + 1, corner + (a[i - 1] === b[j - 1] ? 0 : 1));
      corner = up;
    }
  }
  return row[b.length]!;
}

/** Самое похожее имя — для подсказки «возможно, …»; как на сервере: не дальше трети длины. */
export function closestName(name: string, known: string[]): string | null {
  const wanted = name.trim().toLowerCase();
  let best: string | null = null;
  let distance = Infinity;
  for (const candidate of known) {
    const d = levenshtein(wanted, candidate.toLowerCase());
    if (d < distance) { best = candidate; distance = d; }
  }
  return best !== null && distance <= Math.max(2, Math.floor(wanted.length / 3)) ? best : null;
}
