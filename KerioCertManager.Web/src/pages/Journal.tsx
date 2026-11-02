import { useState } from 'react';
import { api } from '../lib/api';
import { fmtDateTime } from '../lib/format';
import { usePagedList, usePolling } from '../lib/hooks';
import { intlLocale, t } from '../lib/i18n';
import { duration, fingerprint, OUTCOME, shortFingerprint, STEP, TRIGGER } from '../lib/kerio';
import type { Deployment, Outcome } from '../lib/types';
import { Empty, ErrorText, Failed, PageHead, Spinner, Status } from '../ui/bits';
import { Icon } from '../ui/Icon';

const FILTERS: Array<[Outcome | '', string]> = [
  ['', t('Все')],
  ['succeeded', OUTCOME.succeeded.label],
  ['failed', OUTCOME.failed.label],
  ['rolled_back', OUTCOME.rolled_back.label],
  ['dry_run', OUTCOME.dry_run.label],
];

const PAGE = 30;

/** Журнал установок по всем серверам: исход, причина, шаги; идущая установка обновляется сама. */
export function JournalPage() {
  const [filter, setFilter] = useState<Outcome | ''>('');

  const { items, error, loading, hasMore, loadMore, refresh } = usePagedList(async cursor => {
    const query = new URLSearchParams({ limit: String(PAGE), steps: 'true' });
    if (cursor) query.set('before', cursor);
    if (filter) query.set('outcome', filter);
    const page = await api.get<Deployment[]>('/api/kerio/deployments?' + query.toString());
    return { items: page, next: page.length === PAGE ? page[page.length - 1]!.started_at : null };
  }, (d: Deployment) => d.id, filter);

  usePolling(refresh, items?.some(d => d.outcome === 'running') ? 5_000 : 20_000);

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

      {!items ? (error ? <Failed text={error} /> : <Spinner />) : items.length === 0 ? (
        <div className="card"><Empty title={t('Записей нет')} hint={filter ? t('С таким исходом установок не было.') : t('Установок ещё не было.')} /></div>
      ) : (
        <div className="stack tight">
          {items.map(d => <DeploymentCard key={d.id} d={d} />)}
          <ErrorText text={error} />
          {hasMore && (
            <button type="button" disabled={loading} onClick={loadMore}>
              {loading ? t('Загрузка…') : t('Показать ещё')}
            </button>
          )}
        </div>
      )}
    </>
  );
}

/** Установка с раскрываемым журналом шагов. */
function DeploymentCard({ d }: { d: Deployment }) {
  const [open, setOpen] = useState(d.outcome === 'running' || d.outcome === 'failed');
  const meta = OUTCOME[d.outcome];

  return (
    <section className={'card deploy ' + meta.dot}>
      <button type="button" className="deploy-head" onClick={() => setOpen(!open)} aria-expanded={open}>
        <span className={'tl-dot ' + meta.dot} />
        <span className="deploy-title">
          <b>{d.server_name} · {d.common_name}</b>
          <span className="muted small">{fmtDateTime(d.started_at)} · {TRIGGER[d.trigger] ?? d.trigger}{duration(d) && ' · ' + duration(d)}</span>
        </span>
        <Status kind={meta.dot}>{meta.label}</Status>
        <span className={'chev' + (open ? ' open' : '')}><Icon name="chevron" /></span>
      </button>
      {d.error && <p className="note">{d.error}</p>}
      {open && d.steps && (
        <ol className="steps">
          {d.steps.map((step, i) => (
            <li key={i} className={step.ok ? '' : 'bad'}>
              <span className="step-name">{STEP[step.step] ?? step.step}</span>
              <span className="step-msg">{step.message}</span>
              <span className="step-time">{new Date(step.at).toLocaleTimeString(intlLocale())}</span>
            </li>
          ))}
        </ol>
      )}
      {open && (
        <div className="facts small">
          <span>{t('Серийный')} <b className="mono">{d.serial_number.toLowerCase()}</b></span>
          {d.sha256 && <span>SHA-256 <b className="mono" title={fingerprint(d.sha256)}>{shortFingerprint(d.sha256)}</b></span>}
          {d.kerio_certificate_id && <span>{t('В Kerio')} <b className="mono">{d.kerio_certificate_id}</b></span>}
          {d.previous_kerio_certificate_id && <span>{t('Прежний')} <b className="mono">{d.previous_kerio_certificate_id}</b></span>}
        </div>
      )}
    </section>
  );
}
