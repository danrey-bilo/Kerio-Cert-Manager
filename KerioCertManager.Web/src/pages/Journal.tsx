import { useEffect, useState } from 'react';
import { api, Cancelled, errorText } from '../lib/api';
import { t } from '../lib/i18n';
import { OUTCOME } from '../lib/kerio';
import type { Deployment, Outcome } from '../lib/types';
import { Empty, Failed, PageHead, Spinner } from '../ui/bits';
import { DeploymentCard } from './Servers';

const FILTERS: Array<[Outcome | '', string]> = [
  ['', t('Все')],
  ['succeeded', OUTCOME.succeeded.label],
  ['failed', OUTCOME.failed.label],
  ['rolled_back', OUTCOME.rolled_back.label],
  ['dry_run', OUTCOME.dry_run.label],
];

const PAGE = 30;

/** Журнал установок по всем серверам: исход, причина, шаги. */
export function JournalPage() {
  const [filter, setFilter] = useState<Outcome | ''>('');
  const [items, setItems] = useState<Deployment[] | null>(null);
  const [error, setError] = useState('');
  const [more, setMore] = useState(false);
  const [loading, setLoading] = useState(false);

  const load = async (before: string | null, replace: boolean) => {
    setLoading(true);
    try {
      const query = new URLSearchParams({ limit: String(PAGE), steps: 'true' });
      if (before) query.set('before', before);
      if (filter) query.set('outcome', filter);

      const page = await api.get<Deployment[]>('/api/kerio/deployments?' + query.toString());

      setItems(list => (replace || !list ? page : [...list, ...page]));
      setMore(page.length === PAGE);
      setError('');
    } catch (err) {
      if (!(err instanceof Cancelled)) setError(errorText(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { void load(null, true); }, [filter]);

  return (
    <>
      <PageHead title={t('Журнал установок')} sub={t('Каждая установка сертификата на сервер Kerio — с причиной, исходом и шагами')} />

      <div className="pills">
        {FILTERS.map(([value, title]) => (
          <button key={value || 'all'} type="button" className={'pill' + (filter === value ? ' active' : '')} onClick={() => setFilter(value)}>
            {title}
          </button>
        ))}
      </div>

      {error ? <Failed text={error} /> : !items ? <Spinner /> : items.length === 0 ? (
        <div className="card"><Empty title={t('Записей нет')} hint={filter ? t('С таким исходом установок не было.') : t('Установок ещё не было.')} /></div>
      ) : (
        <div className="stack tight">
          {items.map(d => <DeploymentCard key={d.id} d={d} showServer />)}
          {more && (
            <button type="button" disabled={loading} onClick={() => void load(items[items.length - 1]?.started_at ?? null, false)}>
              {loading ? t('Загрузка…') : t('Показать ещё')}
            </button>
          )}
        </div>
      )}
    </>
  );
}
