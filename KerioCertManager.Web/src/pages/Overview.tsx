import { useEffect, useState, type ReactNode } from 'react';
import { api } from '../lib/api';
import { isMobile } from '../lib/device';
import { fmtDate, fmtDateTime } from '../lib/format';
import { useLoad, usePolling } from '../lib/hooks';
import { t } from '../lib/i18n';
import { ago, CERT_STATE, connection, counted, daysText, daysTone, hostOf, ORIGIN, sniName, STATUS } from '../lib/kerio';
import type { KerioServer, Overview, ServerStatus, SourceCert, StoreCert } from '../lib/types';
import { Empty, Failed, PageHead, Spinner, Status } from '../ui/bits';
import { Icon, type IconName } from '../ui/Icon';
import { useOverlay } from '../ui/Overlay';

type Filter = 'all' | 'ok' | 'expiring' | 'expired' | 'foreign';

const FILTER_TITLE: Record<Exclude<Filter, 'all'>, string> = {
  ok: t('в порядке'),
  expiring: t('истекают'),
  expired: t('истекли'),
  foreign: t('не из KMP'),
};

/** Строка обзора: сертификат хранилища или, пока хранилище не прочитано, то, что сервер подаёт по TLS. */
type Row = StoreCert & { tlsOnly?: boolean };

const matches = (r: Row, filter: Filter) =>
  filter === 'all' ? true
    : filter === 'foreign' ? r.origin === 'foreign'
      : counted(r) && r.state === filter;

/** Свёрнутые и развёрнутые карточки браузер помнит до конца сеанса — по умолчанию все свёрнуты. */
const OPEN_KEY = 'overview.open';

function savedOpen(): Record<string, boolean> {
  try { return JSON.parse(sessionStorage.getItem(OPEN_KEY) ?? '{}') as Record<string, boolean>; } catch { return {}; }
}

/** Главная: что за сертификаты на каждом сервере Kerio, откуда они и сколько им осталось; устаревший — «Обновить» у него. */
export function OverviewPage() {
  const { data, error, refresh } = useLoad(() => api.get<Overview>('/api/kerio/overview'));
  const [filter, setFilter] = useState<Filter>('all');
  const [open, setOpen] = useState<Record<string, boolean>>(savedOpen);

  usePolling(refresh, 20_000);

  useEffect(() => {
    try { sessionStorage.setItem(OPEN_KEY, JSON.stringify(open)); } catch { /* без памяти — просто свёрнуто */ }
  }, [open]);

  if (error) return <Failed text={error} />;
  if (!data) return <Spinner />;

  const servers = [...data.servers].sort((a, b) => Number(b.enabled) - Number(a.enabled) || a.name.localeCompare(b.name));
  const active = servers.filter(s => s.enabled);
  const rowsOf = new Map(servers.map(s => [s.id, rows(s, data.warning_days)]));
  const all = active.flatMap(s => rowsOf.get(s.id) ?? []).filter(counted);

  const count = (f: Filter) => all.filter(r => matches(r, f)).length;
  const soonest = all.filter(r => r.state === 'expiring' && r.days_left !== null).sort((a, b) => a.days_left! - b.days_left!)[0];
  const online = active.filter(s => connection(s).kind === 'ok').length;

  const visible = servers
    .map(s => ({ server: s, rows: (rowsOf.get(s.id) ?? []).filter(r => matches(r, filter)) }))
    .filter(x => filter === 'all' || x.rows.length > 0);

  // Очередь установки по всем серверам: что ставится сейчас и сколько ждёт.
  const waiting = servers.reduce((n, s) => n + s.install_queue.length, 0);
  const installingOn = servers.find(s => s.installing);
  const queue = installingOn || waiting > 0 ? { server: installingOn, waiting } : null;

  const isOpen = (id: string) => open[id] ?? filter !== 'all';
  const allOpen = visible.length > 0 && visible.every(x => isOpen(x.server.id));
  const toggleAll = () => setOpen(Object.fromEntries(visible.map(x => [x.server.id, !allOpen])));
  const pick = (f: Filter) => setFilter(current => (current === f ? 'all' : f));

  return (
    <>
      <PageHead
        title={t('Обзор')}
        sub={<>{t('Сертификаты на серверах Kerio Connect — из Key Manager Plus и добавленные вручную · проход каждые {0} мин', data.interval_minutes)}</>}
      />

      {data.source.certificates === 0 && (
        <Banner tone="warn" icon="key" title={t('Key Manager Plus не отдал ни одного сертификата')}
          text={t('Проверьте адрес, ключ API и последний опрос в настройках — без этого обновлять нечем.')}
          href="#/settings/keymanager" action={t('Настроить')} />
      )}
      {data.dry_run && (
        <Banner tone="info" icon="info" title={t('Пробный режим')}
          text={t('Сервис проверяет всё, но сертификаты в Kerio не меняет. Выключите, когда убедитесь в настройке.')}
          href="#/settings/sync" action={t('Настройки')} />
      )}
      {queue && (
        <Banner tone="info" icon="refresh" title={t('Сертификаты ставятся по одному')}
          text={queue.server
            ? t('Сейчас — {0} на «{1}», в очереди ещё {2}. Ход каждой установки — в «Журнале».', queue.server.installing ?? '', queue.server.name, queue.waiting)
            : t('В очереди {0}. Ход каждой установки — в «Журнале».', queue.waiting)}
          href="#/journal" action={t('Журнал')} />
      )}
      {!data.auto_deploy && (
        <Banner tone="warn" icon="pause" title={t('Автоматическая установка на паузе')}
          text={t('Продлённые и только что выбранные сертификаты ждут кнопок «Обновить» и «Добавить» — они в списке сервера, у каждого такого сертификата.')}
          href="#/settings/sync" action={t('Включить')} />
      )}

      {servers.length === 0 ? (
        <div className="card">
          <Empty title={t('Ни одного сервера')} hint={t('Подключите Kerio Connect во вкладке «Серверы» — здесь появится всё, что лежит в его хранилище сертификатов.')}>
            <a className="button primary" href="#/servers">{t('Перейти к серверам')}<Icon name="chevron" /></a>
          </Empty>
        </div>
      ) : (
        <>
          <section className="kpis five" aria-label={t('Сводка и фильтр')}>
            <Kpi icon="server" label={t('Серверов')} value={active.length} hint={online === active.length ? t('все на связи') : t('на связи {0} из {1}', online, active.length)}
              tone={online < active.length ? 'err' : ''} active={filter === 'all'} onClick={() => setFilter('all')} />
            <Kpi icon="check" label={t('В порядке')} value={count('ok')} hint={t('сертификатов')} tone="ok"
              active={filter === 'ok'} onClick={() => pick('ok')} />
            <Kpi icon="clock" label={t('Истекают')} value={count('expiring')} tone={count('expiring') ? 'warn' : ''}
              hint={soonest ? t('ближайший — {0}', daysText(soonest.days_left)) : t('в ближайшие {0} дн. — нет', data.warning_days)}
              active={filter === 'expiring'} onClick={() => pick('expiring')} />
            <Kpi icon="alert" label={t('Истекли')} value={count('expired')} tone={count('expired') ? 'err' : ''} hint={t('сертификатов')}
              active={filter === 'expired'} onClick={() => pick('expired')} />
            <Kpi icon="shield" label={t('Не из KMP')} value={count('foreign')} tone={count('foreign') ? 'warn' : ''} hint={t('добавлены вручную')}
              active={filter === 'foreign'} onClick={() => pick('foreign')} />
          </section>

          <div className="section-head ov-list-head">
            <h2>
              {filter === 'all' ? t('Серверы') : <>{t('Сертификаты: {0}', FILTER_TITLE[filter])}</>}
              <span className="muted"> · {visible.length}</span>
            </h2>
            <div className="ov-list-actions">
              {filter !== 'all' && <button type="button" className="link" onClick={() => setFilter('all')}>{t('Показать все')}</button>}
              {visible.length > 0 && <button type="button" className="link" onClick={toggleAll}>{allOpen ? t('Свернуть все') : t('Развернуть все')}</button>}
            </div>
          </div>

          {visible.length === 0 ? (
            <div className="card"><Empty title={t('Ничего не нашлось')} hint={t('Таких сертификатов сейчас нет ни на одном сервере.')} /></div>
          ) : (
            <div className="stack tight">
              {visible.map(({ server, rows: list }) => (
                <ServerBlock key={server.id} server={server} rows={list} all={rowsOf.get(server.id) ?? []} warning={data.warning_days}
                  filtered={filter !== 'all'} open={isOpen(server.id)} onToggle={() => setOpen(o => ({ ...o, [server.id]: !isOpen(server.id) }))}
                  onChanged={refresh} />
              ))}
            </div>
          )}
        </>
      )}
    </>
  );
}

/** Сертификаты сервера для обзора; хранилище ещё не читали — хотя бы то, что подаётся по TLS. */
function rows(s: KerioServer, warning: number): Row[] {
  if (s.store_checked_at) return s.store;
  if (!s.served?.sha256) return [];
  const bare = (x: string | null | undefined) => (x ?? '').replace(/[^0-9a-f]/gi, '').replace(/^0+/, '').toLowerCase();
  const managed = Boolean(s.source && bare(s.source.serial_number) === bare(s.served.serial));
  const days = s.served.days_left;
  return [{
    id: 'tls',
    type: '',
    subject: s.served.subject ?? '—',
    issuer: s.served.issuer ?? '',
    not_before: s.served.not_before,
    not_after: s.served.not_after,
    days_left: days,
    serial: s.served.serial,
    sha256: s.served.sha256,
    dns_names: [],
    self_signed: false,
    untrusted: false,
    served: true,
    origin: managed ? 'managed' : 'foreign',
    kmp_common_name: null,
    update_from: null,
    state: days === null ? 'unknown' : days < 0 ? 'expired' : days <= warning ? 'expiring' : 'ok',
    tlsOnly: true,
  }];
}

/** Сертификат KMP, который сервис ведёт на сервере: по умолчанию или своего имени. */
interface Managed {
  name: string;
  isDefault: boolean;
  status: ServerStatus;
  text: string;
  source: SourceCert | null;
}

function managedOf(s: KerioServer): Managed[] {
  return [
    ...(s.common_name ? [{ name: s.common_name, isDefault: true, status: s.status, text: s.status_text, source: s.source }] : []),
    ...s.extras.map(e => ({ name: e.common_name, isDefault: false, status: e.status, text: e.status_text, source: e.source })),
  ];
}

function ServerBlock({ server: s, rows: list, all, warning, filtered, open, onToggle, onChanged }: {
  server: KerioServer;
  rows: Row[];
  all: Row[];
  warning: number;
  filtered: boolean;
  open: boolean;
  onToggle: () => void;
  onChanged: () => void;
}) {
  const { confirm, run } = useOverlay();
  const [updating, setUpdating] = useState(false);
  const c = connection(s);
  const tlsOnly = list.some(r => r.tlsOnly);
  const managed = managedOf(s);
  const counting = all.filter(counted);
  const expiring = counting.filter(r => r.state === 'expiring').length;
  const expired = counting.filter(r => r.state === 'expired').length;
  const foreign = all.filter(r => r.origin === 'foreign').length;
  const worst = managed.reduce<Managed | null>((w, m) => (!w || STATUS[m.status].rank < STATUS[w.status].rank ? m : w), null);
  const bodyId = 'ov-body-' + s.id;

  const same = (a: string | null, b: string) => a?.toLowerCase() === b.toLowerCase();
  const position = (name: string) => s.install_queue.findIndex(n => same(n, name)) + 1;
  const inQueue = s.install_queue.length + (s.installing ? 1 : 0);
  const blocked = !s.enabled ? t('Сервер отключён') : !s.password_set ? t('Не задан пароль учётной записи Kerio') : null;

  /** «Обновить» лежащий или «Добавить» недостающий — только сертификат, выбранный для этого сервера. */
  const install = async (name: string, adding: boolean) => {
    const isDefault = s.common_name?.toLowerCase() === name.toLowerCase();
    if (!await confirm(
      adding ? t('Добавить {0} на сервер?', name) : t('Обновить {0}?', name),
      isDefault
        ? t('На «{0}» встанет текущий выпуск из Key Manager Plus как сертификат по умолчанию. Он проверяется на всех портах; при неудаче возвращается прежний.', s.name)
        : t('В хранилище «{0}» ляжет текущий выпуск из Key Manager Plus — Kerio будет подавать его для этого имени. Сертификат по умолчанию не меняется.', s.name),
      adding ? t('Добавить') : t('Обновить'),
    )) return;
    setUpdating(true);
    await run(() => api.post(`/api/kerio/servers/${s.id}/deploy`, { common_name: name }), t('Установка поставлена в очередь — ход виден в «Журнале»'));
    setUpdating(false);
    onChanged();
  };

  const button = (name: string | null, adding: boolean): ReactNode => {
    if (!name) return null;
    if (same(s.installing, name)) return <span className="badge accent ov-installing"><span className="spinner inline-spinner" />{t('Ставится')}</span>;
    if (position(name) > 0) return <span className="badge accent">{t('В очереди · №{0}', position(name))}</span>;
    return (
      <button type="button" className="primary" disabled={updating || blocked !== null}
        title={blocked ?? t('Поставить текущий выпуск из Key Manager Plus сейчас')} onClick={() => void install(name, adding)}>
        <Icon name={adding ? 'plus' : 'refresh'} />{adding ? t('Добавить') : t('Обновить')}
      </button>
    );
  };

  // Выбранные для сервера сертификаты, которых нет в хранилище Kerio, — строкой с «Добавить».
  const present = new Set(s.store.map(z => z.subject.toLowerCase()));
  const missing = s.store_checked_at && !filtered ? managed.filter(m => !present.has(m.name.toLowerCase())) : [];

  // Почему выбранный сертификат не в порядке — под состоянием первой строки его имени.
  const notes = new Map<string, string>();
  for (const m of managed) {
    if (STATUS[m.status].rank > 4 || m.status === 'unchecked') continue;
    const row = list.find(r => same(r.subject, m.name));
    notes.set(row ? row.id : 'missing-' + m.name, m.text);
  }

  // Одним списком: сертификат по умолчанию сверху, затем недостающие выбранные, затем остальное хранилище.
  const ordered: { key: string; row?: Row; m?: Managed }[] = [
    ...missing.filter(m => m.isDefault).map(m => ({ key: 'missing-' + m.name, m })),
    ...list.filter(r => r.served).map(r => ({ key: r.id, row: r })),
    ...missing.filter(m => !m.isDefault).map(m => ({ key: 'missing-' + m.name, m })),
    ...list.filter(r => !r.served).map(r => ({ key: r.id, row: r })),
  ];

  const byName = new Map<string, string[]>();
  for (const m of managed) {
    const sha = s.served_by_name[sniName(m.name)];
    if (sha) byName.set(sha, [...(byName.get(sha) ?? []), m.name]);
  }

  return (
    <section className={'card flush ov-server' + (s.enabled ? '' : ' off') + (open ? ' open' : '')}>
      <button type="button" className="ov-head" onClick={onToggle} aria-expanded={open} aria-controls={bodyId}>
        <span className={'chev' + (open ? ' open' : '')}><Icon name="chevron" /></span>
        <span className={'server-icon ' + c.dot}><Icon name="server" /></span>
        <span className="ov-title">
          <b>{s.name}</b>
          <span className="mono">{hostOf(s.base_url)}</span>
        </span>
        <span className="ov-chips">
          <span className="badge" title={t('Сертификатов в хранилище Kerio')}>{t('{0} в хранилище', s.store_checked_at ? s.store.length : '—')}</span>
          {expired > 0 && <span className="badge err">{expired} {expired === 1 ? t('истёк') : t('истекли')}</span>}
          {expiring > 0 && <span className="badge warn">{expiring} {expiring === 1 ? t('истекает') : t('истекают')}</span>}
          {foreign > 0 && <span className="badge warn">{t('{0} не из KMP', foreign)}</span>}
          {worst && <span className={'badge ' + STATUS[worst.status].tone} title={worst.text}>KMP: {STATUS[worst.status].label}</span>}
          {inQueue > 0 && <span className="badge accent" title={t('Сертификаты ставятся по одному, по порядку')}>{t('очередь установки: {0}', inQueue)}</span>}
        </span>
        <span className="ov-meta">
          <Status kind={c.dot}>{c.label}</Status>
          <span className="muted small" title={fmtDateTime(s.store_checked_at ?? s.last_check_at)}>
            {s.store_checked_at ? t('прочитано {0}', ago(s.store_checked_at)) : t('проверен {0}', ago(s.last_check_at))}
          </span>
        </span>
      </button>

      {open && (
        <div id={bodyId} className="ov-body">
          {managed.length === 0 && s.enabled && (
            <p className="ov-note muted">{t('Сертификаты не выбраны — сервис только следит за сроками. Выбрать — карандашом во вкладке «Серверы».')}</p>
          )}

          {!s.enabled ? (
            <p className="ov-note muted">{t('Сервер отключён — не проверяется. Данные на момент последней проверки.')}</p>
          ) : list.length === 0 && missing.length === 0 ? (
            <p className="ov-note muted">{filtered ? t('Таких сертификатов на этом сервере нет.') : c.kind === 'ok' ? t('Хранилище пусто.') : t('Данных ещё нет: {0}', c.text)}</p>
          ) : (
            <>
              {tlsOnly && (
                <p className="ov-note warn-text">{t('Хранилище Kerio не прочитано{0}. Ниже — только сертификат, который сервер подаёт по TLS.', c.kind !== 'ok' ? ': ' + c.text.replace(/\.$/, '') : '')}</p>
              )}
              {isMobile ? (
                <ul className="ov-list">
                  {ordered.map(({ key, row, m }) => row
                    ? <CertItem key={key} r={row} warning={warning} names={byName.get(row.sha256 ?? '') ?? []} note={notes.get(key)} action={button(row.update_from, false)} />
                    : <MissingItem key={key} m={m!} note={notes.get(key)} action={m!.source ? button(m!.name, true) : null} />)}
                </ul>
              ) : (
                <table className="ov-table">
                  <thead>
                    <tr>
                      <th>{t('Сертификат')}</th>
                      <th>{t('Происхождение')}</th>
                      <th>{t('Действует до')}</th>
                      <th>{t('Состояние')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {ordered.map(({ key, row, m }) => row
                      ? <CertRow key={key} r={row} warning={warning} names={byName.get(row.sha256 ?? '') ?? []} note={notes.get(key)} action={button(row.update_from, false)} />
                      : <MissingRow key={key} m={m!} note={notes.get(key)} action={m!.source ? button(m!.name, true) : null} />)}
                  </tbody>
                </table>
              )}
            </>
          )}
        </div>
      )}
    </section>
  );
}

/** Выбранный для сервера сертификат, которого нет в хранилище Kerio. */
function MissingRow({ m, note, action }: { m: Managed; note?: string; action: ReactNode }) {
  return (
    <tr className="missing">
      <td>
        <span className="cert-name">
          <b className="mono">{m.name}</b>
          {m.isDefault && <span className="badge accent">{t('по умолчанию')}</span>}
        </span>
        <span className="sub">{t('выбран для этого сервера')}</span>
      </td>
      <td>{m.source && <span className="badge info">{t('Есть в KMP')}</span>}</td>
      <td className="nowrap">{m.source?.expires_on ? t('выпуск до {0}', fmtDate(m.source.expires_on)) : '—'}</td>
      <td>
        <Status kind="warn">{t('Нет на сервере')}</Status>
        {note && <span className="sub ov-why">{note}</span>}
        {action && <div className="ov-update">{action}</div>}
      </td>
    </tr>
  );
}

function MissingItem({ m, note, action }: { m: Managed; note?: string; action: ReactNode }) {
  return (
    <li className="ov-cert missing">
      <div className="ov-cert-top">
        <b className="mono">{m.name}</b>
        <Status kind="warn">{t('Нет на сервере')}</Status>
      </div>
      <div className="muted small">{t('выбран для этого сервера')}{m.source?.expires_on && <> · {t('выпуск до {0}', fmtDate(m.source.expires_on))}</>}</div>
      {note && <div className="small ov-why">{note}</div>}
      {action && <div className="ov-update">{action}</div>}
    </li>
  );
}

/** Значки «подаётся»: по умолчанию и для каких имён. */
function ServedBadges({ r, names }: { r: Row; names: string[] }) {
  return (
    <>
      {r.served && <span className="badge accent" title={t('Сертификат по умолчанию: Kerio подаёт его без SNI и для имён без своего сертификата')}>{t('по умолчанию')}</span>}
      {names.filter(n => !r.served || n.toLowerCase() !== r.subject.toLowerCase()).map(n => (
        <span key={n} className="badge accent" title={t('Его Kerio подаёт клиентам, которые обращаются по имени {0}', n)}>{t('подаётся для {0}', n)}</span>
      ))}
    </>
  );
}

function CertRow({ r, warning, names, note, action }: { r: Row; warning: number; names: string[]; note?: string; action: ReactNode }) {
  const origin = ORIGIN[r.origin];
  const state = CERT_STATE[r.state];
  const extra = r.dns_names.filter(n => n.toLowerCase() !== r.subject.toLowerCase());

  return (
    <tr className={r.served || names.length ? 'served' : ''}>
      <td>
        <span className="cert-name">
          <b className="mono">{r.subject || '—'}</b>
          <ServedBadges r={r} names={names} />
        </span>
        <span className="sub">
          {r.issuer && <>{t('выдан {0}', r.issuer)}</>}
          {extra.length > 0 && <span title={extra.join(', ')}> {t('· ещё {0}  {1}', extra.length, extra.length === 1 ? t('имя') : t('имён'))}</span>}
          {r.untrusted && <span className="warn-text"> {t('· Kerio не доверяет цепочке')}</span>}
        </span>
      </td>
      <td>
        <span className={'badge ' + origin.tone} title={origin.hint}>{origin.label}</span>
        {r.origin === 'kmp' && r.kmp_common_name && <span className="sub">{t('в KMP:')} <span className="mono">{r.kmp_common_name}</span></span>}
      </td>
      <td className="nowrap">
        {r.not_after ? fmtDateTime(r.not_after) : '—'}
        <span className={'sub days ' + daysTone(r.days_left, warning)}>{daysText(r.days_left)}</span>
      </td>
      <td>
        <Status kind={state.dot}>{state.label}</Status>
        {note && <span className="sub ov-why">{note}</span>}
        {action && <div className="ov-update">{action}</div>}
      </td>
    </tr>
  );
}

function CertItem({ r, warning, names, note, action }: { r: Row; warning: number; names: string[]; note?: string; action: ReactNode }) {
  const origin = ORIGIN[r.origin];
  const state = CERT_STATE[r.state];

  return (
    <li className={'ov-cert' + (r.served || names.length ? ' served' : '')}>
      <div className="ov-cert-top">
        <b className="mono">{r.subject || '—'}</b>
        <Status kind={state.dot}>{state.label}</Status>
      </div>
      <div className="ov-cert-tags">
        <ServedBadges r={r} names={names} />
        <span className={'badge ' + origin.tone}>{origin.label}</span>
      </div>
      <div className="muted small">{t('до {0} ·', r.not_after ? fmtDate(r.not_after.slice(0, 10)) : '—')} <span className={'days ' + daysTone(r.days_left, warning)}>{daysText(r.days_left)}</span>
        {r.issuer && <> · {r.issuer}</>}
      </div>
      {note && <div className="small ov-why">{note}</div>}
      {action && <div className="ov-update">{action}</div>}
    </li>
  );
}

function Banner({ tone, icon, title, text, href, action }: {
  tone: 'warn' | 'info' | 'err';
  icon: IconName;
  title: string;
  text: string;
  href: string;
  action: string;
}) {
  return (
    <div className={'banner ' + tone}>
      <span className="banner-icon"><Icon name={icon} /></span>
      <div className="banner-text"><b>{title}</b><span>{text}</span></div>
      <a className="button" href={href}>{action}</a>
    </div>
  );
}

function Kpi({ icon, label, value, hint, tone = '', active, onClick }: {
  icon: IconName;
  label: string;
  value: number;
  hint: string;
  tone?: string;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button type="button" className={'kpi clickable ' + tone + (active ? ' active' : '')} aria-pressed={active} onClick={onClick}>
      <span className="kpi-icon"><Icon name={icon} /></span>
      <span className="kpi-label">{label}</span>
      <b className="kpi-value">{value}</b>
      <span className="kpi-hint">{hint}</span>
    </button>
  );
}
