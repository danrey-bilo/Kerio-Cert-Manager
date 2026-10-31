import { useEffect, useState } from 'react';
import { api, Cancelled, errorText } from '../lib/api';
import { fmtDateTime } from '../lib/format';
import { useLoad } from '../lib/hooks';
import { t } from '../lib/i18n';
import { Empty, Failed, PageHead, Spinner, Status } from '../ui/bits';
import { Icon } from '../ui/Icon';
import { useOverlay } from '../ui/Overlay';

interface AuditEntry {
  id: number;
  ts: string;
  actor_id: string | null;
  actor_type: string;
  actor_ip: string | null;
  action: string;
  module: string | null;
  resource_type: string | null;
  resource_id: string | null;
  resource_name: string | null;
  result: string;
  reason: string | null;
  changes: Record<string, unknown> | null;
}

interface AuditPage { items: AuditEntry[]; next_cursor: string | null }

interface AuditActions { groups: Array<{ key: string; title: string | null; actions: string[] }>; titles: Record<string, string> }

const ACTOR: Record<string, string> = { user: t('Пользователь'), system: t('Сервис'), api_token: t('Ключ API'), User: t('Пользователь'), System: t('Сервис') };

/** Журнал действий: кто, что и когда сделал — только на дозапись, с цепочкой хешей. */
export function AuditPage() {
  const catalog = useLoad(() => api.get<AuditActions>('/api/audit/actions'));
  const [module, setModule] = useState('');
  const [items, setItems] = useState<AuditEntry[] | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const { run } = useOverlay();

  const load = async (from: string | null, replace: boolean) => {
    setLoading(true);
    try {
      const query = new URLSearchParams({ limit: '50' });
      if (module) query.set('module', module);
      if (from) query.set('cursor', from);
      const page = await api.get<AuditPage>('/api/audit?' + query.toString());
      setItems(list => (replace || !list ? page.items : [...list, ...page.items]));
      setCursor(page.next_cursor);
      setError('');
    } catch (err) {
      if (!(err instanceof Cancelled)) setError(errorText(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { void load(null, true); }, [module]);

  const exportCsv = () => run(
    () => api.download('/api/audit/export?format=csv' + (module ? '&module=' + encodeURIComponent(module) : ''), {}, 'audit.csv'),
    t('Выгрузка готова'),
  );

  const titles = catalog.data?.titles ?? {};

  return (
    <>
      <PageHead title={t('Журнал действий')} sub={t('Кто, что и когда сделал. Журнал только дописывается и держится цепочкой хешей')}>
        <button onClick={() => void exportCsv()}><Icon name="download" />{t('Выгрузить CSV')}</button>
      </PageHead>

      <div className="pills">
        <button type="button" className={'pill' + (module === '' ? ' active' : '')} onClick={() => setModule('')}>{t('Все')}</button>
        {catalog.data?.groups.map(g => (
          <button key={g.key} type="button" className={'pill' + (module === g.key ? ' active' : '')} onClick={() => setModule(g.key)}>
            {g.title ?? g.key}
          </button>
        ))}
      </div>

      {error ? <Failed text={error} /> : !items ? <Spinner /> : items.length === 0 ? (
        <div className="card"><Empty title={t('Записей нет')} /></div>
      ) : (
        <div className="card flush">
          <ul className="timeline audit">
            {items.map(e => (
              <li key={e.id}>
                <div className="tl-item">
                  <span className={'tl-dot ' + (e.result === 'allow' ? 'ok' : 'err')} />
                  <div className="tl-main">
                    <div className="tl-title">
                      <b>{titles[e.action] ?? e.action}</b>
                      {e.resource_name && <span className="muted">· {e.resource_name}</span>}
                    </div>
                    <div className="tl-sub">
                      <span>{ACTOR[e.actor_type] ?? e.actor_type}{e.actor_id ? ' ' + e.actor_id.slice(-6) : ''}</span>
                      {e.actor_ip && <span className="mono">{e.actor_ip}</span>}
                      <span className="mono">{e.action}</span>
                      {e.result !== 'allow' && <Status kind="err">{t('отказано')}</Status>}
                    </div>
                    {e.reason && <p className="tl-error muted-text">{e.reason}</p>}
                    {e.changes && Object.keys(e.changes).length > 0 && (
                      <p className="audit-changes mono">{Object.entries(e.changes).map(([k, v]) => k + ': ' + (typeof v === 'string' ? v : JSON.stringify(v))).join(' · ')}</p>
                    )}
                  </div>
                  <span className="tl-time">{fmtDateTime(e.ts)}</span>
                </div>
              </li>
            ))}
          </ul>
          {cursor && (
            <div className="load-more">
              <button type="button" disabled={loading} onClick={() => void load(cursor, false)}>{loading ? t('Загрузка…') : t('Показать ещё')}</button>
            </div>
          )}
        </div>
      )}
    </>
  );
}
