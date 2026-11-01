import { useEffect, useMemo, useState, type FormEvent, type ReactNode } from 'react';
import { api, Cancelled, errorText } from '../lib/api';
import { isMobile } from '../lib/device';
import { fmtDateTime } from '../lib/format';
import { useLoad } from '../lib/hooks';
import { t } from '../lib/i18n';
import { ago, EVENT, splitList } from '../lib/kerio';
import { loadSettings, saveSettings, text, type Settings } from '../lib/settings';
import type { KerioServer, MailStatus, SentNotification } from '../lib/types';
import { Empty, ErrorText, Failed, PageHead, Spinner, Status, type DotKind } from '../ui/bits';
import { Icon } from '../ui/Icon';
import { Invalid } from '../ui/Modal';
import { useOverlay } from '../ui/Overlay';

const LOCALES: Array<[string, string]> = [['ru', 'Русский'], ['uk', 'Українська'], ['en', 'English']];

const LEVEL: Record<SentNotification['level'], DotKind> = { info: 'ok', warning: 'warn', critical: 'err' };

/** Переключатели событий: ключ настройки, название, когда приходит письмо. */
const SWITCHES = {
  expiring: 'kerio.alert_expiring',
  expired: 'kerio.alert_expired',
  overdue: 'kerio.alert_overdue',
  failed: 'kerio.alert_deploy_failed',
  deployed: 'kerio.alert_deployed',
  pending: 'kerio.alert_pending',
  unreachable: 'kerio.alert_unreachable',
  access: 'kerio.alert_access',
  drift: 'kerio.alert_drift',
  missing: 'kerio.alert_source_missing',
} as const;

type SwitchKey = keyof typeof SWITCHES;

/** Вкладка «Оповещения»: когда и кому писать, пробное письмо, история отправленного. */
export function AlertsPage() {
  const page = useLoad(() => Promise.all([
    loadSettings(),
    api.get<KerioServer[]>('/api/kerio/servers'),
    api.get<MailStatus>('/api/mail').catch(() => null),
  ]));

  if (page.error) return <Failed text={page.error} />;
  if (!page.data) return <Spinner />;

  const [settings, servers, mail] = page.data;
  return <AlertsForm settings={settings} servers={servers} mail={mail} onSaved={page.reload} />;
}

function AlertsForm({ settings, servers, mail, onSaved }: {
  settings: Settings;
  servers: KerioServer[];
  mail: MailStatus | null;
  onSaved: () => void;
}) {
  const { toast } = useOverlay();
  const initial = useMemo(() => ({
    switches: Object.fromEntries(Object.entries(SWITCHES).map(([k, key]) => [k, settings[key]?.value !== false])) as Record<SwitchKey, boolean>,
    warning: text(settings, 'kerio.warning_days'),
    reminders: text(settings, 'kerio.alert_reminder_days'),
    overdueHours: text(settings, 'kerio.alert_overdue_hours'),
    unreachableChecks: text(settings, 'kerio.alert_unreachable_checks'),
    repeat: text(settings, 'kerio.alert_repeat_hours'),
    emails: text(settings, 'kerio.alert_emails'),
    locale: text(settings, 'kerio.alert_locale') || 'ru',
    perServer: Object.fromEntries(servers.map(s => [s.id, s.notify_emails.join(', ')])) as Record<string, string>,
  }), [settings, servers]);

  const [form, setForm] = useState(initial);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [testing, setTesting] = useState(false);
  const [historyVersion, setHistoryVersion] = useState(0);

  useEffect(() => setForm(initial), [initial]);

  const dirty = JSON.stringify(form) !== JSON.stringify(initial);
  const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) => setForm(f => ({ ...f, [key]: value }));
  const toggle = (key: SwitchKey) => (value: boolean) => setForm(f => ({ ...f, switches: { ...f.switches, [key]: value } }));

  const submit = async (event?: FormEvent) => {
    event?.preventDefault();
    setError('');
    setBusy(true);
    try {
      const reminders = splitList(form.reminders).map(Number);
      if (reminders.some(d => !Number.isInteger(d) || d < 1 || d > 365)) throw new Invalid(t('Напоминания — дни от 1 до 365 через запятую, например «7, 3, 1».'));

      await saveSettings(settings, {
        ...Object.fromEntries(Object.entries(SWITCHES).map(([k, key]) => [key, form.switches[k as SwitchKey]])),
        'kerio.warning_days': form.warning ? Number(form.warning) : '',
        'kerio.alert_reminder_days': [...new Set(reminders)].sort((a, b) => b - a).join(', '),
        'kerio.alert_overdue_hours': form.overdueHours ? Number(form.overdueHours) : '',
        'kerio.alert_unreachable_checks': form.unreachableChecks ? Number(form.unreachableChecks) : '',
        'kerio.alert_repeat_hours': form.repeat === '' ? '' : Number(form.repeat),
        'kerio.alert_emails': form.emails.trim(),
        'kerio.alert_locale': form.locale,
      });

      for (const s of servers) {
        const next = splitList(form.perServer[s.id] ?? '');
        if (next.join(',') !== s.notify_emails.join(',')) {
          await api.patch(`/api/kerio/servers/${s.id}`, { notify_emails: next });
        }
      }

      toast(t('Оповещения сохранены'));
      onSaved();
    } catch (err) {
      if (!(err instanceof Cancelled)) setError(err instanceof Invalid ? err.message : errorText(err));
    } finally {
      setBusy(false);
    }
  };

  const test = async () => {
    setTesting(true);
    try {
      const result = await api.post<{ recipients: string[] }>('/api/kerio/notifications/test');
      toast(t('Пробное письмо ушло: {0}', result.recipients.join(', ')));
      setHistoryVersion(v => v + 1);
    } catch (err) {
      if (!(err instanceof Cancelled)) toast(errorText(err), 'err');
    } finally {
      setTesting(false);
    }
  };

  const recipients = splitList(form.emails);

  return (
    <>
      <PageHead title={t('Оповещения')} sub={t('Когда и кому сервис пишет о сертификатах и серверах Kerio Connect')}>
        <button type="button" onClick={test} disabled={testing || !mail?.configured} title={mail?.configured ? t('Письмо общим получателям') : t('Сначала настройте почту')}>
          <Icon name="send" />{testing ? t('Отправка…') : t('Пробное письмо')}
        </button>
      </PageHead>

      {mail && !mail.configured && (
        <div className="banner warn">
          <span className="banner-icon"><Icon name="mail" /></span>
          <div className="banner-text"><b>{t('Почта не настроена — оповещения не уходят')}</b><span>{t('Укажите сервер SMTP, и письма пойдут по правилам ниже.')}</span></div>
          <a className="button" href="#/settings/mail">{t('Настроить почту')}</a>
        </div>
      )}
      {mail?.configured && mail.failed > 0 && (
        <div className="banner err">
          <span className="banner-icon"><Icon name="alert" /></span>
          <div className="banner-text"><b>{t('Не ушло писем: {0}', mail.failed)}</b><span>{mail.last_error ?? t('Сервер SMTP отказал.')}</span></div>
          <a className="button" href="#/settings/mail">{t('Почта')}</a>
        </div>
      )}

      <form onSubmit={submit} className="stack">
        <section className="card setting-group">
          <h2><Icon name="bell" />{t('Когда писать')}</h2>

          <Rule title={t('Сертификат скоро истекает')} level="warn" on={form.switches.expiring} onChange={toggle('expiring')}
            hint={t('Про сертификат, который сервер подаёт сейчас. С этого же срока он помечается «истекающим» в обзоре.')}>
            <Param label={t('Первое письмо за')}>
              <div className="unit-input"><input type="number" min={1} max={180} value={form.warning} onChange={e => set('warning', e.target.value)} /><span>{t('дн.')}</span></div>
            </Param>
            <Param label={t('Напоминания за')}>
              <div className="unit-input wide"><input type="text" inputMode="numeric" placeholder="7, 3, 1" value={form.reminders} onChange={e => set('reminders', e.target.value)} /><span>{t('дн.')}</span></div>
            </Param>
          </Rule>

          <Rule title={t('Сертификат истёк')} level="err" on={form.switches.expired} onChange={toggle('expired')}
            hint={t('Сервер подаёт просроченный сертификат: клиенты почты уже видят ошибку.')} />

          <Rule title={t('Не обновился сам')} level="err" on={form.switches.overdue} onChange={toggle('overdue')}
            hint={t('В Key Manager Plus есть продление, а на сервере всё ещё прежний: установка выключена, не проходит или сервер недоступен.')}>
            <Param label={t('Если ждёт дольше')}>
              <div className="unit-input"><input type="number" min={1} max={720} value={form.overdueHours} onChange={e => set('overdueHours', e.target.value)} /><span>{t('ч')}</span></div>
            </Param>
          </Rule>

          <Rule title={t('Установка не удалась или откачена')} level="err" on={form.switches.failed} onChange={toggle('failed')}
            hint={t('С причиной и шагом, на котором остановилось; повтор по тому же выпуску — не чаще интервала ниже.')} />

          <Rule title={t('Сертификат установлен')} level="ok" on={form.switches.deployed} onChange={toggle('deployed')}
            hint={t('Одна установка — письмо сразу: что, куда, отпечаток, до какого числа. Очередь из нескольких — одно сводное письмо, когда она пройдена.')} />

          <Rule title={t('Ждёт ручной установки')} level="warn" on={form.switches.pending} onChange={toggle('pending')}
            hint={t('Есть продление, а автоматическая установка для сервера или всего сервиса выключена.')} />

          <Rule title={t('Сервер недоступен')} level="err" on={form.switches.unreachable} onChange={toggle('unreachable')}
            hint={t('Не отвечает по сети или API Kerio раз за разом.')}>
            <Param label={t('После неудачных проверок подряд')}>
              <div className="unit-input"><input type="number" min={1} max={100} value={form.unreachableChecks} onChange={e => set('unreachableChecks', e.target.value)} /><span>{t('раз')}</span></div>
            </Param>
          </Rule>

          <Rule title={t('Доступ к Kerio')} level="err" on={form.switches.access} onChange={toggle('access')}
            hint={t('Kerio отверг пароль учётной записи, у неё нет прав или сертификат Kerio не прошёл проверку доверия.')} />

          <Rule title={t('Сертификат сменили вручную')} level="warn" on={form.switches.drift} onChange={toggle('drift')}
            hint={t('Сервер подаёт не тот сертификат, что поставил сервис, — правка мимо Kerio Cert Manager.')} />

          <Rule title={t('Нет в Key Manager Plus')} level="warn" on={form.switches.missing} onChange={toggle('missing')}
            hint={t('Сертификат, к которому привязан сервер, пропал из опроса Key Manager Plus.')} />

          <div className="setting-row">
            <div className="setting-label">
              <span>{t('Повторять, пока не исправлено')}</span>
              <small>{t('Если условие всё ещё верно, письмо повторится не чаще. 0 — одно письмо на каждое появление.')}</small>
            </div>
            <div className="unit-input"><input type="number" min={0} max={720} value={form.repeat} onChange={e => set('repeat', e.target.value)} /><span>{t('ч')}</span></div>
          </div>
        </section>

        <section className="card setting-group">
          <h2><Icon name="users" />{t('Кому писать')}</h2>
          <div className="setting-row column">
            <div className="setting-label">
              <span>{t('Получатели всех оповещений')}</span>
              <small>{recipients.length
                ? t('Через запятую.')
                : t('Пусто — письма получают все активные пользователи панели.')}</small>
            </div>
            <textarea rows={2} placeholder="it@example.com, noc@example.com" value={form.emails} onChange={e => set('emails', e.target.value)} />
          </div>
          <div className="setting-row">
            <div className="setting-label">
              <span>{t('Язык писем')}</span>
              <small>{t('Тема, рамка и подробности событий — на этом языке.')}</small>
            </div>
            <select className="narrow" value={form.locale} onChange={e => set('locale', e.target.value)}>
              {LOCALES.map(([v, t]) => <option key={v} value={v}>{t}</option>)}
            </select>
          </div>
          {servers.length > 0 && (
            <div className="setting-row column">
              <div className="setting-label">
                <span>{t('Дополнительно по серверам')}</span>
                <small>{t('Эти адреса получают письма только о своём сервере — вместе с общими получателями.')}</small>
              </div>
              <div className="per-server">
                {servers.map(s => (
                  <label key={s.id} className="per-server-row">
                    <span className="per-server-name"><Icon name="server" /><b>{s.name}</b></span>
                    <input type="text" inputMode="email" placeholder={t('только общие получатели')} value={form.perServer[s.id] ?? ''}
                      onChange={e => set('perServer', { ...form.perServer, [s.id]: e.target.value })} />
                  </label>
                ))}
              </div>
            </div>
          )}
        </section>

        {dirty && (
          <div className="savebar">
            <span className="savebar-text">{t('Есть несохранённые изменения')}</span>
            <ErrorText text={error} />
            <div className="savebar-actions">
              <button type="button" onClick={() => { setForm(initial); setError(''); }} disabled={busy}>{t('Отменить')}</button>
              <button type="submit" className="primary" disabled={busy}>{busy ? t('Сохранение…') : t('Сохранить')}</button>
            </div>
          </div>
        )}
      </form>

      <History key={historyVersion} />
    </>
  );
}

function Rule({ title, hint, level, on, onChange, children }: {
  title: string;
  hint: string;
  level: DotKind;
  on: boolean;
  onChange: (value: boolean) => void;
  children?: ReactNode;
}) {
  return (
    <div className={'setting-row rule' + (on ? '' : ' off')}>
      <div className="rule-main">
        <div className="setting-label">
          <span className="rule-title"><span className={'dot ' + level} />{title}</span>
          <small>{hint}</small>
        </div>
        {children && on && <div className="rule-params">{children}</div>}
      </div>
      <label className="switch" title={on ? t('Выключить') : t('Включить')}>
        <input type="checkbox" checked={on} onChange={e => onChange(e.target.checked)} aria-label={title} />
        <span />
      </label>
    </div>
  );
}

function Param({ label, children }: { label: string; children: ReactNode }) {
  return <div className="param"><span>{label}</span>{children}</div>;
}

const PAGE = 30;

function History() {
  const [items, setItems] = useState<SentNotification[] | null>(null);
  const [error, setError] = useState('');
  const [more, setMore] = useState(false);
  const [loading, setLoading] = useState(false);

  const load = async (before: string | null) => {
    setLoading(true);
    try {
      const query = new URLSearchParams({ limit: String(PAGE) });
      if (before) query.set('before', before);
      const page = await api.get<SentNotification[]>('/api/kerio/notifications?' + query.toString());
      setItems(list => (before && list ? [...list, ...page] : page));
      setMore(page.length === PAGE);
      setError('');
    } catch (err) {
      if (!(err instanceof Cancelled)) setError(errorText(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { void load(null); }, []);

  return (
    <>
      <div className="section-head"><h2>{t('Отправленные оповещения')}</h2><span className="muted small">{t('хранятся 180 дней')}</span></div>
      {error ? <Failed text={error} /> : !items ? <Spinner /> : items.length === 0 ? (
        <div className="card"><Empty title={t('Писем ещё не было')} hint={t('Здесь появится каждое оповещение: о чём, о каком сервере и кому ушло.')} /></div>
      ) : (
        <div className="card flush">
          {isMobile ? (
            <ul className="timeline">
              {items.map(n => (
                <li key={n.id}>
                  <div className="tl-item">
                    <span className={'tl-dot ' + LEVEL[n.level]} />
                    <div className="tl-main">
                      <div className="tl-title"><b>{n.title}</b></div>
                      <div className="tl-sub">
                        <span>{EVENT[n.event] ?? n.event}</span>
                        {n.server_name && <span>{n.server_name}</span>}
                        <span title={n.recipients.join(', ')}>{t('{0} адрес.', n.recipients.length)}</span>
                      </div>
                    </div>
                    <span className="tl-time" title={fmtDateTime(n.at)}>{ago(n.at)}</span>
                  </div>
                </li>
              ))}
            </ul>
          ) : (
            <table>
              <thead>
                <tr><th>{t('Когда')}</th><th>{t('Событие')}</th><th>{t('Сервер')}</th><th>{t('Письмо')}</th><th>{t('Кому')}</th></tr>
              </thead>
              <tbody>
                {items.map(n => (
                  <tr key={n.id}>
                    <td className="nowrap" title={fmtDateTime(n.at)}>{fmtDateTime(n.at)}<span className="sub">{ago(n.at)}</span></td>
                    <td className="nowrap"><Status kind={LEVEL[n.level]}>{EVENT[n.event] ?? n.event}</Status></td>
                    <td>{n.server_name ?? <span className="muted">—</span>}</td>
                    <td className="note-cell">{n.title}</td>
                    <td className="recipients-cell" title={n.recipients.join(', ')}>
                      {n.recipients.slice(0, 2).join(', ')}{n.recipients.length > 2 && <span className="muted"> {t('и ещё {0}', n.recipients.length - 2)}</span>}
                      {n.queued < n.recipients.length && <span className="sub err-text">{t('в очередь принято {0} из {1}', n.queued, n.recipients.length)}</span>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {more && (
            <div className="load-more">
              <button type="button" disabled={loading} onClick={() => void load(items[items.length - 1]?.at ?? null)}>
                {loading ? t('Загрузка…') : t('Показать ещё')}
              </button>
            </div>
          )}
        </div>
      )}
    </>
  );
}
