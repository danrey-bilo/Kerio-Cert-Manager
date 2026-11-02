import { useMemo, useState } from 'react';
import { api, Cancelled, errorText } from '../lib/api';
import { isMobile } from '../lib/device';
import { useLoad } from '../lib/hooks';
import { loadSettings, saveSettings, type Settings } from '../lib/settings';
import type { Me } from '../lib/types';
import { Banner, ErrorText, Failed, PageHead, Spinner, Switch } from '../ui/bits';
import { Icon, type IconName } from '../ui/Icon';
import { useOverlay } from '../ui/Overlay';
import { StepUpDialog } from '../ui/StepUp';
import { intlLocale, t } from '../lib/i18n';

interface Item {
  key: string;
  label: string;
  hint?: string;
  unit?: string;
}

interface Category {
  id: string;
  title: string;
  icon: IconName;
  items: Item[];
}

/** Настройки защиты; одна кнопка «Сохранить», уходят только изменённые ключи. */
const CATEGORIES: Category[] = [
  {
    id: 'step-up',
    title: t('Подтверждение кодом'),
    icon: 'lock',
    items: [
      { key: 'auth.step_up_minutes', label: t('Не спрашивать код повторно'), hint: t('После кода из приложения или входа'), unit: t('мин') },
    ],
  },
  {
    id: 'sign-in',
    title: t('Вход'),
    icon: 'users',
    items: [
      { key: 'auth.lockout_threshold', label: t('Попыток до блокировки'), hint: t('Неверный пароль или код подряд') },
      { key: 'auth.lockout_minutes', label: t('Блокировка'), unit: t('мин') },
      { key: 'auth.two_factor_challenge_minutes', label: t('Время на ввод кода'), hint: t('Между паролем и кодом при входе'), unit: t('мин') },
    ],
  },
  {
    id: 'sessions',
    title: t('Сессии'),
    icon: 'refresh',
    items: [
      { key: 'auth.session_idle_minutes', label: t('Выход при бездействии'), unit: t('мин') },
      { key: 'auth.session_absolute_hours', label: t('Сессия живёт не дольше'), hint: t('Потом — вход заново'), unit: t('ч') },
    ],
  },
  {
    id: 'passwords',
    title: t('Пароли'),
    icon: 'key',
    items: [
      { key: 'auth.password_min_length', label: t('Минимальная длина'), unit: t('симв.') },
      { key: 'auth.password_require_uppercase', label: t('Заглавная буква') },
      { key: 'auth.password_require_lowercase', label: t('Строчная буква') },
      { key: 'auth.password_require_digit', label: t('Цифра') },
      { key: 'auth.password_require_symbol', label: t('Спецсимвол') },
    ],
  },
  {
    id: 'links',
    title: t('Ссылки и письма'),
    icon: 'mail',
    items: [
      { key: 'auth.invitation_ttl_minutes', label: t('Ссылка приглашения действует'), unit: t('мин') },
      { key: 'mail.rate_per_hour', label: t('Писем в час на один адрес'), hint: t('Защита ящиков от потока писем') },
    ],
  },
];

const TIME = new Intl.DateTimeFormat(intlLocale(), { timeStyle: 'short' });

/** Значение настройки для поля: флажок как есть, остальное строкой. */
function initial(value: unknown): string | boolean {
  if (typeof value === 'boolean') return value;
  return value === null || value === undefined ? '' : String(value);
}

export function SecurityPage() {
  const { data, error, reload } = useLoad(() => Promise.all([loadSettings(), api.get<Me>('/api/me')]));

  if (error) return <Failed text={error} />;
  if (!data) return <Spinner />;

  const [settings, me] = data;
  const version = JSON.stringify(Object.values(settings).map(s => s.value)) + (me.step_up_until ?? '');
  return <SecurityForm key={version} settings={settings} me={me} onSaved={reload} />;
}

function SecurityForm({ settings, me, onSaved }: { settings: Settings; me: Me; onSaved: () => void }) {
  const { show, toast } = useOverlay();
  const categories = useMemo(
    () => CATEGORIES
      .map(c => ({ ...c, items: c.items.filter(i => settings[i.key]) }))
      .filter(c => c.items.length > 0),
    [settings],
  );
  const start = useMemo(
    () => Object.fromEntries(categories.flatMap(c => c.items).map(i => [i.key, initial(settings[i.key]?.value)])),
    [categories, settings],
  );
  const [values, setValues] = useState<Record<string, string | boolean>>(start);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const changed = Object.keys(start).filter(key => values[key] !== start[key]);
  const minutes = Number(settings['auth.step_up_minutes']?.value ?? 10);
  const until = me.step_up_until ? new Date(me.step_up_until) : null;
  const fresh = until !== null && until.getTime() > Date.now();

  const confirmNow = async () => {
    if (await show(close => <StepUpDialog minutes={minutes} onClose={close} />)) { toast(t('Подтверждено')); onSaved(); }
  };

  const save = async () => {
    setError('');
    setBusy(true);
    try {
      await saveSettings(settings, Object.fromEntries(changed.map(key => {
        const value = values[key] ?? '';
        return [key, typeof value === 'boolean' ? value : value.trim() === '' ? '' : Number(value)];
      })));
      toast(t('Сохранено'));
      onSaved();
    } catch (err) {
      if (!(err instanceof Cancelled)) setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  const jump = (id: string) => document.getElementById('sec-' + id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });

  return (
    <>
      <PageHead title={t('Безопасность')} sub={t('Вход, сессии и пароли. Изменения подтверждаются кодом из приложения.')} />

      <Banner tone={fresh ? 'ok' : ''} icon={fresh ? 'check' : 'lock'}
        title={fresh && until ? t('Код подтверждён до {0}', TIME.format(until)) : t('Важные действия спросят код')}
        text={t('Скачивание сертификатов и ключей, установка на Kerio, изменения серверов, настроек и людей.')}>
        {!fresh && <button type="button" onClick={confirmNow}>{t('Подтвердить')}</button>}
      </Banner>

      <div className="settings-layout">
        {!isMobile && (
          <nav className="cat-nav" aria-label={t('Категории')}>
            {categories.map(c => (
              <button key={c.id} type="button" onClick={() => jump(c.id)}>
                <Icon name={c.icon} />{c.title}
              </button>
            ))}
          </nav>
        )}

        <div className="settings-body">
          {categories.map(c => (
            <section key={c.id} id={'sec-' + c.id} className="card setting-group">
              <h2><Icon name={c.icon} />{c.title}</h2>
              {c.items.map(item => {
                const meta = settings[item.key];
                if (!meta) return null;
                const value = values[item.key];
                return (
                  <div key={item.key} className="setting-row">
                    <div className="setting-label">
                      <span>{item.label}</span>
                      {item.hint && <small>{item.hint}</small>}
                    </div>
                    {typeof meta.value === 'boolean' ? (
                      <Switch label={item.label} checked={value === true} onChange={on => setValues(v => ({ ...v, [item.key]: on }))} />
                    ) : (
                      <div className="unit-input">
                        <input
                          type="number"
                          aria-label={item.label}
                          min={meta.minimum ?? undefined}
                          max={meta.maximum ?? undefined}
                          placeholder={meta.default === null || meta.default === undefined ? '' : String(meta.default)}
                          value={String(value ?? '')}
                          onChange={e => setValues(v => ({ ...v, [item.key]: e.target.value }))}
                        />
                        <span>{item.unit ?? ''}</span>
                      </div>
                    )}
                  </div>
                );
              })}
            </section>
          ))}
        </div>
      </div>

      {changed.length > 0 && (
        <div className="savebar" role="region" aria-label={t('Несохранённые изменения')}>
          <span className="savebar-text">{t('Изменено: {0}', changed.length)}</span>
          <ErrorText text={error} />
          <div className="savebar-actions">
            <button type="button" onClick={() => { setValues(start); setError(''); }}>{t('Отменить')}</button>
            <button type="button" className="primary" disabled={busy} onClick={save}>{t('Сохранить')}</button>
          </div>
        </div>
      )}
    </>
  );
}
