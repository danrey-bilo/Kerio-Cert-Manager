import { useMemo, useState } from 'react';
import { api } from '../lib/api';
import { isMobile } from '../lib/device';
import { fmtDate, fmtDateTime } from '../lib/format';
import { useLoad } from '../lib/hooks';
import { t } from '../lib/i18n';
import { daysFromDate, daysText, daysTone, sameName } from '../lib/kerio';
import type { KeyManagerStatus, KnownCertificate, Overview } from '../lib/types';
import { Empty, Failed, PageHead, Spinner } from '../ui/bits';
import { Icon } from '../ui/Icon';

type Order = 'name' | 'expiry-asc' | 'expiry-desc';

const ORDERS: Array<[Order, string]> = [
  ['name', t('По имени')],
  ['expiry-asc', t('Сначала истекающие')],
  ['expiry-desc', t('Сначала дальние')],
];

/** Порядок помнит браузер, не сервер. */
const ORDER_KEY = 'certificates.order';

function savedOrder(): Order {
  try {
    const value = localStorage.getItem(ORDER_KEY);
    return value === 'expiry-asc' || value === 'expiry-desc' ? value : 'name';
  } catch {
    return 'name';
  }
}

const byName = (a: KnownCertificate, b: KnownCertificate) =>
  a.common_name.localeCompare(b.common_name) || (b.expires_on ?? '').localeCompare(a.expires_on ?? '');

/** Без срока — в конце при любом направлении. */
const byExpiry = (direction: 1 | -1) => (a: KnownCertificate, b: KnownCertificate) => {
  if (!a.expires_on || !b.expires_on) return a.expires_on ? -1 : b.expires_on ? 1 : byName(a, b);
  return direction * a.expires_on.localeCompare(b.expires_on) || byName(a, b);
};

const COMPARE: Record<Order, (a: KnownCertificate, b: KnownCertificate) => number> = {
  name: byName,
  'expiry-asc': byExpiry(1),
  'expiry-desc': byExpiry(-1),
};

function Expiry({ ymd, warning }: { ymd: string | null; warning: number }) {
  const days = daysFromDate(ymd);
  if (days === null) return <span className="muted">—</span>;
  return (
    <span className="expiry">
      {fmtDate(ymd)}
      <span className={'badge ' + daysTone(days, warning)}>{daysText(days)}</span>
    </span>
  );
}

export function CertificatesPage() {
  const { data, error } = useLoad(() => Promise.all([
    api.get<KeyManagerStatus>('/api/keymanager'),
    api.get<KnownCertificate[]>('/api/keymanager/certificates'),
    api.get<Overview>('/api/kerio/overview'),
  ]));
  const [query, setQuery] = useState('');
  const [order, setOrder] = useState<Order>(savedOrder);

  const choose = (next: Order) => {
    setOrder(next);
    try { localStorage.setItem(ORDER_KEY, next); } catch { /* без хранилища — порядок до перезагрузки */ }
  };

  const rows = useMemo(() => {
    if (!data) return [];
    const q = query.trim().toLowerCase();
    return data[1]
      .filter(c => !q || c.common_name.toLowerCase().includes(q) || c.serial_number.toLowerCase().includes(q))
      .sort(COMPARE[order]);
  }, [data, query, order]);

  if (error) return <Failed text={error} />;
  if (!data) return <Spinner />;

  const [km, known, { servers, warning_days: warning }] = data;

  /** Серверы Kerio, на которые уходит выпуск: по умолчанию или как сертификат имени. */
  const usedBy = (c: KnownCertificate) => {
    return servers.flatMap(s =>
      sameName(s.common_name, c.common_name) ? [{ server: s, role: t('по умолчанию') }]
        : s.extras.some(e => sameName(e.common_name, c.common_name)) ? [{ server: s, role: t('по имени') }]
          : []);
  };

  const used = (c: KnownCertificate) => {
    const list = usedBy(c);
    return list.length === 0
      ? <span className="muted">—</span>
      : <span className="chips">{list.map(({ server: s, role }) => (
        <a key={s.id} className="chip link-chip" href={'#/servers/' + s.id} title={t('Сертификат {0} — настройки сервера', role)}>{s.name}</a>
      ))}</span>;
  };

  const sub = km.configured
    ? <>{t('Key Manager Plus · опрос {0}{1}', fmtDateTime(km.last_poll_at), km.last_poll_error ? t(' · ошибка') : '')}</>
    : <>{t('Key Manager Plus не подключён —')} <a href="#/settings/keymanager">{t('настроить')}</a></>;

  return (
    <>
      <PageHead title={t('Сертификаты')} sub={sub} />

      {known.length === 0 ? (
        <div className="card">
          <Empty title={t('Сертификатов пока нет')} hint={t('Они появятся после опроса Key Manager Plus.')} />
        </div>
      ) : (
        <div className="card flush">
          <div className="toolbar">
            <label className="search">
              <Icon name="search" />
              <input type="search" placeholder={t('Поиск по имени или серийному')} value={query} onChange={e => setQuery(e.target.value)} />
            </label>
            <div className="inline">
              {isMobile && (
                <select className="order" aria-label={t('Порядок')} value={order} onChange={e => choose(e.target.value as Order)}>
                  {ORDERS.map(([value, title]) => <option key={value} value={value}>{title}</option>)}
                </select>
              )}
              <span className="muted small">{t('{0} из {1}', rows.length, known.length)}</span>
            </div>
          </div>
          {rows.length === 0 && <Empty title={t('Ничего не найдено')} />}
          {rows.length > 0 && isMobile && (
            <div className="list">
              {rows.map(c => (
                <div key={c.common_name + c.serial_number} className="item">
                  <div className="item-main">
                    <div className="item-title">{c.common_name}</div>
                    <div className="item-meta"><Expiry ymd={c.expires_on} warning={warning} /></div>
                    <div className="item-meta mono">{c.serial_number}</div>
                    {usedBy(c).length > 0 && <div className="item-meta">{used(c)}</div>}
                  </div>
                </div>
              ))}
            </div>
          )}
          {rows.length > 0 && !isMobile && (
            <table>
              <thead>
                <tr>
                  <th aria-sort={order === 'name' ? 'ascending' : 'none'}>
                    <button type="button" className="sort" title={t('По имени')} onClick={() => choose('name')}>{t('Сертификат')}<Icon name={order === 'name' ? 'up' : 'sort'} />
                    </button>
                  </th>
                  <th aria-sort={order === 'expiry-asc' ? 'ascending' : order === 'expiry-desc' ? 'descending' : 'none'}>
                    <button
                      type="button"
                      className="sort"
                      title={order === 'expiry-asc' ? t('Сначала дальние') : t('Сначала истекающие')}
                      onClick={() => choose(order === 'expiry-asc' ? 'expiry-desc' : 'expiry-asc')}
                    >{t('Действует до')}<Icon name={order === 'expiry-asc' ? 'up' : order === 'expiry-desc' ? 'down' : 'sort'} />
                    </button>
                  </th>
                  <th>{t('На серверах Kerio')}</th>
                  <th>{t('Замечен')}</th>
                </tr>
              </thead>
              <tbody>
                {rows.map(c => (
                  <tr key={c.common_name + c.serial_number}>
                    <td>
                      <span className="strong">{c.common_name}</span>
                      <span className="sub mono">{c.serial_number}</span>
                    </td>
                    <td><Expiry ymd={c.expires_on} warning={warning} /></td>
                    <td>{used(c)}</td>
                    <td className="muted">{fmtDateTime(c.first_seen_at)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}
    </>
  );
}
