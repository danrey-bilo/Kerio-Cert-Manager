
export type Locale = 'ru' | 'en' | 'uk';

/** Названия языков — на них самих: найти свой язык должен тот, кто текущего не читает. */
export const LANGUAGES: ReadonlyArray<{ code: Locale; name: string }> = [
  { code: 'ru', name: 'Русский' },
  { code: 'en', name: 'English' },
  { code: 'uk', name: 'Українська' },
];

/** Личная настройка на сервере (модуль настроек). */
export const LOCALE_SETTING = 'ui.locale';

const STORE = 'kcm.locale';

let current: Locale = 'ru';
let dict: Record<string, string> = {};

export const isLocale = (value: unknown): value is Locale => value === 'ru' || value === 'en' || value === 'uk';

function saved(): Locale | null {
  try {
    const value = localStorage.getItem(STORE);
    return isLocale(value) ? value : null;
  } catch {
    return null;
  }
}

function browser(): Locale {
  for (const tag of navigator.languages ?? [navigator.language]) {
    const code = tag.slice(0, 2).toLowerCase();
    if (isLocale(code)) return code;
  }
  return 'ru';
}

/** До первого кадра: язык (сохранённый, иначе браузера) и его словарь. */
export async function initLocale(): Promise<void> {
  current = saved() ?? browser();
  document.documentElement.lang = current;
  if (current === 'en') dict = (await import('../locales/en.json')).default;
  else if (current === 'uk') dict = (await import('../locales/uk.json')).default;
}

export const locale = (): Locale => current;

/** Тег для Intl: даты и числа — по языку интерфейса. */
export const intlLocale = (): string => ({ ru: 'ru-RU', en: 'en-GB', uk: 'uk-UA' })[current];

/** Перевод с подстановками по номерам: t('Найдено {0} серверов', n). */
export function t(text: string, ...args: unknown[]): string {
  const template = dict[text] || text;
  if (args.length === 0) return template;
  return template.replace(/\{(\d+)\}/g, (match, index: string) => {
    const i = Number(index);
    if (i >= args.length) return match;
    const value = args[i];
    return value === null || value === undefined || typeof value === 'boolean' ? '' : String(value);
  });
}

/** Слово при числе: формы русские (один, два, пять) — переводятся каждая; правило — языка интерфейса. */
export function plural(n: number, one: string, few: string, many: string): string {
  const kind = new Intl.PluralRules(intlLocale()).select(n);
  return kind === 'one' ? t(one) : kind === 'few' ? t(few) : t(many);
}

/** Запомнить язык; другой — перезагрузить страницу. */
export function applyLocale(next: Locale) {
  try {
    localStorage.setItem(STORE, next);
  } catch {
    return;
  }
  if (next !== current && saved() === next) location.reload();
}
