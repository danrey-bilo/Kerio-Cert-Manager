import { useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { api, Cancelled, errorText } from '../lib/api';
import { isMobile } from '../lib/device';
import { fmtDate, fmtDateTime } from '../lib/format';
import { intlLocale, plural, t } from '../lib/i18n';
import { useLoad, usePolling } from '../lib/hooks';
import {
  ago, closestName, connection, daysUntil, daysText, duration, fingerprint, hostOf, OUTCOME, shortFingerprint, splitList, STATUS, STEP, TRIGGER,
} from '../lib/kerio';
import type { Deployment, KerioServer, KnownCertificate, LoginTest, Probe, ServerStatus } from '../lib/types';
import { Check, CodeInput, Empty, ErrorText, Failed, Field, PageHead, Spinner, Status } from '../ui/bits';
import { Icon } from '../ui/Icon';
import { FormModal, Invalid } from '../ui/Modal';
import { useOverlay } from '../ui/Overlay';

/** Раздел «Серверы» — управление подключениями: связь, синхронизация, подключение, правка, удаление. */
export function ServersPage({ id }: { id?: string }) {
  return <ServersList openWizard={id === 'new'} editId={id && id !== 'new' ? id : undefined} />;
}

function ServersList({ openWizard, editId }: { openWizard: boolean; editId?: string }) {
  const { data, error, reload, refresh } = useLoad(() => api.get<KerioServer[]>('/api/kerio/servers'));
  const { show, run, toast } = useOverlay();
  const [syncing, setSyncing] = useState(false);
  const [checking, setChecking] = useState<string | null>(null);
  const opened = useRef(false);

  const add = async () => {
    const created = await new Promise<string | null>(resolve => {
      void show(close => <ConnectWizard onClose={id => { close(Boolean(id)); resolve(id); }} />);
    });
    if (location.hash !== '#/servers') history.replaceState(null, '', '#/servers');
    if (created) { toast(t('Сервер подключён — первая проверка уже идёт')); reload(); }
  };

  const edit = async (server: KerioServer) => {
    await show(close => <ServerDialog server={server} onClose={done => { close(done); reload(); }} />);
    if (location.hash !== '#/servers') history.replaceState(null, '', '#/servers');
  };

  const remove = async (server: KerioServer) => {
    if (await show(close => <DeleteDialog server={server} onClose={close} />)) {
      toast(t('«{0}» удалён', server.name));
      reload();
    }
  };

  const check = async (server: KerioServer) => {
    setChecking(server.id);
    try {
      const fresh = await api.post<KerioServer>(`/api/kerio/servers/${server.id}/check`);
      const c = connection(fresh);
      toast(c.kind === 'ok' ? t('«{0}»: связь есть, вход в Kerio работает', fresh.name) : `«${fresh.name}»: ${c.text}`, c.kind === 'ok' ? undefined : 'err');
      reload();
    } catch (err) {
      if (!(err instanceof Cancelled)) toast(errorText(err), 'err');
    } finally {
      setChecking(null);
    }
  };

  const sync = async () => {
    if (!await run(() => api.post('/api/keymanager/poll'), t('Синхронизация запущена: опрос KMP и проверка серверов'))) return;
    setSyncing(true);
    setTimeout(() => setSyncing(false), 30_000);
  };

  useEffect(() => {
    if (opened.current) return;
    if (openWizard) { opened.current = true; void add(); return; }
    const target = editId && data?.find(s => s.id === editId);
    if (target) { opened.current = true; void edit(target); }
  }, [openWizard, editId, data]);

  usePolling(refresh, syncing || data?.some(s => s.busy) ? 5_000 : 15_000);

  if (error) return <Failed text={error} />;
  if (!data) return <Spinner />;

  const servers = [...data].sort((a, b) => a.name.localeCompare(b.name));
  const down = servers.filter(s => s.enabled && connection(s).dot === 'err').length;

  return (
    <>
      <PageHead
        title={t('Серверы Kerio Connect')}
        sub={servers.length === 0
          ? t('Подключения к серверам и какой сертификат из Key Manager Plus ставить на каждый')
          : <>{servers.length} {plural(servers.length, 'сервер', 'сервера', 'серверов')} · {down ? <span className="err-text">{t('{0} без связи', down)}</span> : t('все на связи')}</>}
      >
        <button onClick={sync} disabled={syncing}>
          {syncing ? <span className="spinner inline-spinner" /> : <Icon name="refresh" />}
          {syncing ? t('Синхронизация…') : t('Синхронизировать')}
        </button>
        <button className="primary" onClick={add}><Icon name="plus" />{t('Подключить сервер')}</button>
      </PageHead>

      {servers.length === 0 ? (
        <div className="card">
          <Empty
            title={t('Серверов пока нет')}
            hint={t('Понадобятся адрес администрирования (обычно https://сервер:4040) и отдельная учётная запись Kerio с правами полного администратора.')}
          >
            <button className="primary" onClick={add}><Icon name="plus" />{t('Подключить первый сервер')}</button>
          </Empty>
        </div>
      ) : isMobile ? (
        <div className="stack tight">
          {servers.map(s => (
            <ConnectionCard key={s.id} server={s} checking={checking === s.id}
              onCheck={() => check(s)} onEdit={() => edit(s)} onDelete={() => remove(s)} />
          ))}
        </div>
      ) : (
        <div className="card flush">
          <table className="servers-table">
            <thead>
              <tr>
                <th>{t('Сервер')}</th>
                <th>{t('Подключение')}</th>
                <th>{t('Проверен')}</th>
                <th aria-label={t('Действия')} />
              </tr>
            </thead>
            <tbody>
              {servers.map(s => {
                const c = connection(s);
                return (
                  <tr key={s.id} className={s.enabled ? '' : 'off'}>
                    <td>
                      <div className="server-cell">
                        <span className={'server-icon ' + c.dot}><Icon name="server" /></span>
                        <div>
                          <b>{s.name}</b>
                          <span className="sub mono">{hostOf(s.base_url)}</span>
                          <span className="sub">{sslSummary(s)}</span>
                        </div>
                      </div>
                    </td>
                    <td className="conn-cell">
                      <Status kind={s.busy ? 'warn' : c.dot} strong>{s.busy ? t('Работает…') : c.label}</Status>
                      {c.text && <span className={'sub' + (c.dot === 'err' ? ' err-text' : '')}>{c.text}</span>}
                    </td>
                    <td className="muted nowrap" title={fmtDateTime(s.last_check_at)}>{ago(s.last_check_at)}</td>
                    <td className="actions">
                      <div className="row-icons">
                        <button type="button" className="icon" title={t('Проверить связь сейчас')} aria-label={t('Проверить {0}', s.name)}
                          disabled={checking === s.id || s.busy} onClick={() => check(s)}>
                          {checking === s.id ? <span className="spinner inline-spinner" /> : <Icon name="pulse" />}
                        </button>
                        <button type="button" className="icon" title={t('Изменить: сертификат SSL и подключение')} aria-label={t('Изменить {0}', s.name)}
                          onClick={() => edit(s)}>
                          <Icon name="edit" />
                        </button>
                        <button type="button" className="icon danger" title={t('Удалить сервер')} aria-label={t('Удалить {0}', s.name)}
                          disabled={s.busy} onClick={() => remove(s)}>
                          <Icon name="trash" />
                        </button>
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

/** Строка-напоминание о настройке SSL: какой сертификат и как ставится. */
function sslSummary(s: KerioServer): string {
  const total = (s.common_name ? 1 : 0) + s.extras.length;
  if (total === 0) return t('Сертификаты KMP не выбраны — только наблюдение');
  const names = [s.common_name, ...s.extras.map(e => e.common_name)].filter(Boolean) as string[];
  const list = names.length <= 2 ? names.join(', ') : t('{0} и ещё {1}', names.slice(0, 2).join(', '), names.length - 2);
  return `KMP: ${list}` + (s.auto_deploy ? t(' · автоустановка') : t(' · установка вручную'));
}

function ConnectionCard({ server: s, checking, onCheck, onEdit, onDelete }: {
  server: KerioServer;
  checking: boolean;
  onCheck: () => void;
  onEdit: () => void;
  onDelete: () => void;
}) {
  const c = connection(s);

  return (
    <section className={'card conn-card ' + c.dot + (s.enabled ? '' : ' off')}>
      <div className="server-card-head">
        <span className={'server-icon ' + c.dot}><Icon name="server" /></span>
        <div className="server-card-title">
          <b>{s.name}</b>
          <span>{hostOf(s.base_url)}</span>
        </div>
        <span className={'pill-status ' + (s.busy ? 'warn' : c.dot)}>
          {s.busy && <span className="pulse" />}
          {s.busy ? t('Работает…') : c.label}
        </span>
      </div>
      {c.text && <p className={'small' + (c.dot === 'err' ? ' err-text' : ' muted')}>{c.text}</p>}
      <p className="muted small">{t('{0} · проверен {1}', sslSummary(s), ago(s.last_check_at))}</p>
      <div className="conn-actions">
        <button type="button" onClick={onCheck} disabled={checking || s.busy}><Icon name="pulse" />{checking ? t('Проверка…') : t('Проверить')}</button>
        <button type="button" onClick={onEdit}><Icon name="edit" />{t('Изменить')}</button>
        <button type="button" className="danger" onClick={onDelete} disabled={s.busy} aria-label={t('Удалить {0}', s.name)}><Icon name="trash" /></button>
      </div>
    </section>
  );
}

/** Код второго фактора — на каждое удаление: сервер проверяет, что он свежий. */
function DeleteDialog({ server: s, onClose }: { server: KerioServer; onClose: (done: boolean) => void }) {
  const [code, setCode] = useState('');

  return (
    <FormModal
      title={t('Удалить «{0}»?', s.name)}
      note={<>{t('Сервис перестанет проверять и обновлять {0}. На самом Kerio ничего не изменится, история установок останется в журнале. Подтвердите удаление кодом из приложения-аутентификатора.', hostOf(s.base_url))}</>}
      submit={t('Удалить сервер')}
      danger
      onSubmit={async () => {
        if (!/^\d{6,8}$/.test(code)) throw new Invalid(t('Введите код из приложения-аутентификатора.'));
        await api.stepUp(code);
        await api.del(`/api/kerio/servers/${s.id}`);
      }}
      onClose={onClose}
    >
      <CodeInput value={code} onChange={setCode} />
    </FormModal>
  );
}

const bare = (serial: string | null | undefined) => (serial ?? '').replace(/[^0-9a-f]/gi, '').replace(/^0+/, '').toLowerCase();

type Tab = 'ssl' | 'connection';

/** Текущий выпуск KMP по Common Name. */
interface KmpRelease {
  commonName: string;
  serial: string;
  expiresOn: string | null;
}

/** Покрывает ли имя сертификата адрес сервера (точно или подстановкой одного уровня). */
function covers(commonName: string, host: string): boolean {
  const cn = commonName.trim().toLowerCase();
  const h = host.trim().toLowerCase();
  if (!cn || !h) return false;
  if (cn === h) return true;
  if (!cn.startsWith('*.')) return false;
  const rest = cn.slice(1);
  return h.endsWith(rest) && !h.slice(0, -rest.length).includes('.');
}

/** Чем плох вписанный сертификат; null — годится. Повторяет проверку сервера, чтобы причина была видна до сохранения. */
function problemOf(name: string, kmp: Map<string, KmpRelease>, managed: Set<string>, loaded: boolean): string | null {
  if (/\s/.test(name)) return t('в имени пробел — имя сертификата пишется без пробелов');
  if (kmp.has(name.toLowerCase()) || managed.has(name.toLowerCase()) || !loaded) return null;
  if (kmp.size === 0) return t('Key Manager Plus не отдал ни одного сертификата — проверьте связь с KMP в настройках');
  const hint = closestName(name, [...kmp.values()].map(r => r.commonName));
  return t('нет в Key Manager Plus — проверьте написание{0}', (hint ? t(' (возможно, {0})', hint) : ''));
}

function ServerDialog({ server: s, onClose }: { server: KerioServer; onClose: (done: boolean) => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const { show, toast } = useOverlay();
  const [tab, setTab] = useState<Tab>('ssl');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const initialNames = [...(s.common_name ? [s.common_name] : []), ...s.extras.map(e => e.common_name)];
  const [names, setNames] = useState<string[]>(initialNames);
  const [primary, setPrimary] = useState(s.common_name ?? '');
  const [draft, setDraft] = useState('');
  const [draftError, setDraftError] = useState('');
  const [auto, setAuto] = useState(s.auto_deploy);
  const [cleanup, setCleanup] = useState(s.cleanup_old);
  const [ports, setPorts] = useState(s.verify_ports.join(', '));

  const [name, setName] = useState(s.name);
  const [url, setUrl] = useState(s.base_url);
  const [username, setUsername] = useState(s.username);
  const [password, setPassword] = useState('');
  const [enabled, setEnabled] = useState(s.enabled);
  const [trust, setTrust] = useState({ mode: s.tls_mode, pin: s.tls_pin_sha256, name: s.tls_server_name });

  const kmp = useLoad(() => api.get<KnownCertificate[]>('/api/keymanager/certificates'));

  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) dialog.showModal();
    return () => dialog?.close();
  }, []);

  const host = useMemo(() => { try { return new URL(s.base_url).hostname; } catch { return ''; } }, [s.base_url]);

  /** Текущий выпуск каждого имени KMP — замеченный позже всех. */
  const releases = useMemo(() => {
    const map = new Map<string, KmpRelease>();
    for (const c of [...(kmp.data ?? [])].sort((a, b) => a.first_seen_at.localeCompare(b.first_seen_at))) {
      map.set(c.common_name.toLowerCase(), { commonName: c.common_name, serial: c.serial_number, expiresOn: c.expires_on });
    }
    return map;
  }, [kmp.data]);

  const managed = useMemo(() => new Set(initialNames.map(n => n.toLowerCase())), [s]);
  const status = useMemo(() => {
    const map = new Map<string, { status: ServerStatus; text: string }>();
    if (s.common_name) map.set(s.common_name.toLowerCase(), { status: s.status, text: s.status_text });
    for (const e of s.extras) map.set(e.common_name.toLowerCase(), { status: e.status, text: e.status_text });
    return map;
  }, [s]);

  const problems = names.map(n => problemOf(n, releases, managed, Boolean(kmp.data)));
  const suggestions = [...releases.values()].map(r => r.commonName).filter(n => !names.some(x => x.toLowerCase() === n.toLowerCase())).sort();

  const moved = url.trim() !== s.base_url || username.trim().toLowerCase() !== s.username.toLowerCase();
  const extrasOf = (list: string[], main: string) => list.filter(n => n.toLowerCase() !== main.toLowerCase());

  const certsChanged = primary !== (s.common_name ?? '') || extrasOf(names, primary).join('\n') !== s.extras.map(e => e.common_name).join('\n');
  const changed =
    certsChanged || auto !== s.auto_deploy || cleanup !== s.cleanup_old
    || ports.replace(/\s/g, '') !== s.verify_ports.join(',') || name.trim() !== s.name || moved
    || Boolean(password) || enabled !== s.enabled;

  const add = () => {
    const value = draft.trim().replace(/\.$/, '');
    setDraftError('');
    setError('');
    if (!value) return;
    if (names.some(n => n.toLowerCase() === value.toLowerCase())) { setDraftError(t('«{0}» уже в списке.', value)); return; }
    setNames(list => [...list, releases.get(value.toLowerCase())?.commonName ?? value]);
    setDraft('');
  };

  const remove = (n: string) => {
    setError('');
    setNames(list => list.filter(x => x !== n));
    if (primary === n) setPrimary('');
  };

  const portList = () => {
    const list = splitList(ports).map(Number);
    if (list.some(p => !Number.isInteger(p) || p < 1 || p > 65535)) throw new Invalid(t('Порты — числа от 1 до 65535 через запятую.'));
    return list;
  };

  const save = async () => {
    const bad = names.map((n, i) => [n, problems[i]] as const).filter(([, p]) => p);
    if (bad.length) {
      setTab('ssl');
      throw new Invalid(t('Не сохранено: {0}.', bad.map(([n, p]) => `«${n}» — ${p}`).join('; ')));
    }
    if (moved && !password) { setTab('connection'); throw new Invalid(t('Адрес или логин изменились — введите пароль заново.')); }
    if (!name.trim()) { setTab('connection'); throw new Invalid(t('Дайте серверу имя.')); }
    await api.patch(`/api/kerio/servers/${s.id}`, {
      name: name.trim(),
      base_url: url.trim(),
      username: username.trim(),
      password: password || null,
      ...(certsChanged ? {
        common_name: primary || null,
        clear_common_name: !primary,
        extra_common_names: extrasOf(names, primary),
      } : {}),
      auto_deploy: auto,
      cleanup_old: cleanup,
      verify_ports: portList(),
      enabled,
    });
  };

  const guard = async (action: () => Promise<void>) => {
    setError('');
    setBusy(true);
    try { await action(); } catch (err) {
      if (!(err instanceof Cancelled)) setError(err instanceof Invalid ? err.message : errorText(err));
    } finally { setBusy(false); }
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    void guard(async () => {
      if (changed) await save();
      onClose(changed);
      if (changed) toast(t('Сохранено — сервер проверяется заново'));
    });
  };

  const reTrust = async () => {
    const fresh = await new Promise<KerioServer | null>(resolve => {
      void show(close => <TrustDialog server={s} onClose={updated => { close(Boolean(updated)); resolve(updated); }} />);
    });
    if (fresh) setTrust({ mode: fresh.tls_mode, pin: fresh.tls_pin_sha256, name: fresh.tls_server_name });
  };

  const servedSerial = bare(s.served?.serial);

  return (
    <dialog ref={ref} className="wide server-dialog" onCancel={e => { e.preventDefault(); onClose(false); }}>
      <form noValidate onSubmit={submit}>
        <div>
          <h2>{s.name}</h2>
          <p className="muted small mono">{hostOf(s.base_url)}</p>
          <nav className="tabs dialog-tabs" aria-label={t('Разделы настроек сервера')}>
            <button type="button" className={tab === 'ssl' ? 'active' : ''} aria-current={tab === 'ssl'} onClick={() => setTab('ssl')}>
              <Icon name="cert" />{t('Сертификаты SSL')}{names.length > 0 && <span className="pill-count">{names.length}</span>}
            </button>
            <button type="button" className={tab === 'connection' ? 'active' : ''} aria-current={tab === 'connection'} onClick={() => setTab('connection')}>
              <Icon name="server" />{t('Подключение')}</button>
          </nav>
        </div>

        {tab === 'ssl' && (
          <>
            <p className="muted small">{t('Сертификаты из Key Manager Plus, которые сервис держит на этом Kerio свежими. Kerio подаёт сертификат по имени, которое назвал клиент;')} <b>{t('по умолчанию')}</b> {t('— когда имя не названо или для него нет своего сертификата.')}</p>

            <div className="cert-list" role="list" aria-label={t('Сертификаты сервера')}>
              <label className={'cert-row default-none' + (primary === '' ? ' checked' : '')} role="listitem">
                <input type="radio" name="primary" checked={primary === ''} onChange={() => setPrimary('')} />
                <span className="cert-row-main">
                  <b>{t('Сертификат по умолчанию не менять')}</b>
                  <span className="muted small">{t('Сервис ставит только сертификаты имён, а сертификат по умолчанию оставляет как есть.')}</span>
                </span>
              </label>
              {names.map((n, i) => {
                const release = releases.get(n.toLowerCase());
                const problem = problems[i];
                const state = status.get(n.toLowerCase());
                const days = daysUntil(release?.expiresOn ? release.expiresOn + 'T00:00:00Z' : null);
                const isPrimary = primary.toLowerCase() === n.toLowerCase();
                const onServer = isPrimary && Boolean(release) && bare(release?.serial) === servedSerial;
                return (
                  <div key={n} className={'cert-row' + (isPrimary ? ' checked' : '') + (problem ? ' invalid' : '')} role="listitem">
                    <input type="radio" name="primary" checked={isPrimary} disabled={Boolean(problem)}
                      aria-label={t('Сделать {0} сертификатом по умолчанию', n)} onChange={() => setPrimary(n)} />
                    <span className="cert-row-main">
                      <b className="mono">{n}</b>
                      {problem
                        ? <span className="small err-text">{problem}</span>
                        : <span className="muted small">
                            {release ? <>{t('текущий выпуск до {0} · {1}', fmtDate(release.expiresOn), daysText(days))}</> : t('нет в последнем опросе Key Manager Plus')}
                            {covers(n, host) && t(' · подходит к адресу сервера')}
                          </span>}
                    </span>
                    <span className="pick-badges">
                      {isPrimary && <span className="badge accent">{t('по умолчанию')}</span>}
                      {!problem && state && !certsChangedFor(n, s) && (
                        <span className={'badge ' + STATUS[state.status].tone} title={state.text}>
                          {onServer ? t('стоит на сервере') : STATUS[state.status].label}
                        </span>
                      )}
                      {!problem && !state && <span className="badge">{t('новый')}</span>}
                    </span>
                    <button type="button" className="icon danger" title={t('Убрать {0} из списка', n)} aria-label={t('Убрать {0}', n)} onClick={() => remove(n)}>
                      <Icon name="x" />
                    </button>
                  </div>
                );
              })}
              {names.length === 0 && <p className="muted small pick-empty">{t('Сертификатов пока нет — сервис только наблюдает за сроком того, что подаёт сервер.')}</p>}
            </div>

            <div className="add-row">
              <input type="text" list={'kmp-names-' + s.id} placeholder={kmp.data ? t('Имя сертификата из Key Manager Plus') : t('Загрузка списка Key Manager Plus…')}
                value={draft} onChange={e => { setDraft(e.target.value); setDraftError(''); }}
                onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); add(); } }} aria-label={t('Имя сертификата')} />
              <datalist id={'kmp-names-' + s.id}>{suggestions.map(n => <option key={n} value={n} />)}</datalist>
              <button type="button" onClick={add}><Icon name="plus" />{t('Добавить')}</button>
            </div>
            {draftError && <p className="error">{draftError}</p>}
            {kmp.error && <p className="note">{kmp.error}</p>}

            <div className="toggles">
              <div className="setting-row compact">
                <div className="setting-label">
                  <span>{t('Ставить продлённые автоматически')}</span>
                  <small>{t('Выключено — продления ждут письма и кнопки «Обновить» во вкладке «Обзор»; только что добавленный сертификат ставится сразу.')}</small>
                </div>
                <label className="switch"><input type="checkbox" checked={auto} onChange={e => setAuto(e.target.checked)} /><span /></label>
              </div>
              <div className="setting-row compact">
                <div className="setting-label">
                  <span>{t('Убирать старые версии сертификатов')}</span>
                  <small>{t('После удачной установки — и по умолчанию, и по имени; одна предыдущая остаётся для ручного возврата. Убираются только свои версии: из Key Manager Plus или поставленные сервисом; купленные и положенные вручную не трогаются. Своя версия имени, что действует дольше новой, убирается всегда — иначе Kerio подавал бы её.')}</small>
                </div>
                <label className="switch"><input type="checkbox" checked={cleanup} onChange={e => setCleanup(e.target.checked)} /><span /></label>
              </div>
            </div>
            <Field label={t('Порты для проверки после установки')}
              hint={t('Порты служб с TLS сразу (443, 993, 995, 465, 636): новый сертификат должен появиться на каждом')}>
              <input type="text" inputMode="numeric" placeholder="443, 993" value={ports} onChange={e => setPorts(e.target.value)} />
            </Field>
          </>
        )}

        {tab === 'connection' && (
          <>
            {s.auth_rejected && (
              <div className="probe warn">
                <div className="probe-head">
                  <Icon name="alert" />
                  <div><b>{t('Kerio отверг пароль')}</b><span className="muted small">{t('Введите пароль учётной записи заново — до этого сервис в Kerio не входит.')}</span></div>
                </div>
              </div>
            )}
            <div className="row">
              <Field label={t('Имя')}><input type="text" value={name} onChange={e => setName(e.target.value)} /></Field>
              <Field label={t('Адрес администрирования')}><input type="text" inputMode="url" value={url} onChange={e => setUrl(e.target.value)} /></Field>
            </div>
            <div className="row">
              <Field label={t('Логин Kerio')}><input type="text" autoComplete="off" value={username} onChange={e => setUsername(e.target.value)} /></Field>
              <Field label={t('Пароль')} hint={moved ? t('Обязателен: адрес или логин изменились') : t('Пусто — без изменений')}>
                <input type="password" autoComplete="new-password" placeholder={s.password_set ? t('•••••••• задан') : t('не задан')} value={password} onChange={e => setPassword(e.target.value)} />
              </Field>
            </div>
            <div className="trust-line">
              <div className="setting-label">
                <span>{t('Доверие к сертификату Kerio')}</span>
                <small>
                  {trust.mode === 'pin'
                    ? <>{t('Закреплённый отпечаток')} <span className="mono" title={fingerprint(trust.pin)}>{shortFingerprint(trust.pin)}</span></>
                    : <>{t('Центр сертификации')}{trust.name && <>{t(', имя')} <span className="mono">{trust.name}</span></>}</>}
                </small>
              </div>
              <button type="button" onClick={reTrust}><Icon name="shield" />{t('Проверить заново')}</button>
            </div>
            <div className="setting-row compact">
              <div className="setting-label">
                <span>{t('Сервер включён')}</span>
                <small>{t('Выключенный не проверяется и не обновляется; настройки и история сохраняются.')}</small>
              </div>
              <label className="switch"><input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} /><span /></label>
            </div>
          </>
        )}

        <ErrorText text={error} />
        <div className="actions-row">
          <button type="button" onClick={() => onClose(false)}>{t('Отмена')}</button>
          <button type="submit" className="primary" disabled={busy}>{busy ? t('Подождите…') : t('Сохранить')}</button>
        </div>
      </form>
    </dialog>
  );
}

/** Состояние с сервера относится к сохранённой роли имени: сменили «по умолчанию» — значок до сохранения не показываем. */
function certsChangedFor(name: string, s: KerioServer): boolean {
  const wasPrimary = (s.common_name ?? '').toLowerCase() === name.toLowerCase();
  const wasExtra = s.extras.some(e => e.common_name.toLowerCase() === name.toLowerCase());
  return !wasPrimary && !wasExtra;
}

/** Установка с раскрываемым журналом шагов. */
export function DeploymentCard({ d, showServer = false }: { d: Deployment; showServer?: boolean }) {
  const [open, setOpen] = useState(d.outcome === 'running' || d.outcome === 'failed');
  const meta = OUTCOME[d.outcome];

  return (
    <section className={'card deploy ' + meta.dot}>
      <button type="button" className="deploy-head" onClick={() => setOpen(!open)} aria-expanded={open}>
        <span className={'tl-dot ' + meta.dot} />
        <div className="deploy-title">
          <b>{showServer ? d.server_name + ' · ' : ''}{d.common_name}</b>
          <span className="muted small">{fmtDateTime(d.started_at)} · {TRIGGER[d.trigger] ?? d.trigger}{duration(d) && ' · ' + duration(d)}</span>
        </div>
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

type Step = 1 | 2 | 3;

function ConnectWizard({ onClose }: { onClose: (id: string | null) => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const [step, setStep] = useState<Step>(1);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const [name, setName] = useState('');
  const [url, setUrl] = useState('');
  const [probe, setProbe] = useState<Probe | null>(null);
  const [mode, setMode] = useState<'ca' | 'pin'>('pin');
  const [serverName, setServerName] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [login, setLogin] = useState<LoginTest | null>(null);
  const [kmp, setKmp] = useState<string[]>([]);
  const [commonName, setCommonName] = useState('');
  const [auto, setAuto] = useState(true);
  const [ports, setPorts] = useState('');
  const [cleanup, setCleanup] = useState(false);

  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) dialog.showModal();
    api.get<KnownCertificate[]>('/api/keymanager/certificates')
      .then(list => setKmp([...new Set(list.map(c => c.common_name))].sort()))
      .catch(() => setKmp([]));
    return () => dialog?.close();
  }, []);

  const normalizedUrl = () => {
    const text = url.trim();
    return /^https?:\/\//i.test(text) ? text : 'https://' + text;
  };

  const guard = async (action: () => Promise<void>) => {
    setError('');
    setBusy(true);
    try { await action(); } catch (err) {
      if (!(err instanceof Cancelled)) setError(err instanceof Invalid ? err.message : errorText(err));
    } finally { setBusy(false); }
  };

  const next = (event: FormEvent) => {
    event.preventDefault();

    if (step === 1) {
      void guard(async () => {
        if (!name.trim()) throw new Invalid(t('Дайте серверу понятное имя, например «Почта — главный офис».'));
        if (!url.trim()) throw new Invalid(t('Укажите адрес администрирования Kerio.'));
        const p = await api.post<Probe>('/api/kerio/servers/probe', { base_url: normalizedUrl() });
        setProbe(p);
        setPorts(p.same_certificate_ports.join(', '));
        setMode(p.suggested_mode);
        setServerName(p.suggested_server_name ?? '');
        setStep(2);
      });
      return;
    }

    if (step === 2) {
      void guard(async () => {
        if (!username.trim() || !password) throw new Invalid(t('Нужны логин и пароль учётной записи Kerio.'));
        const result = await api.post<LoginTest>('/api/kerio/servers/test', {
          base_url: normalizedUrl(),
          username: username.trim(),
          password,
          tls_mode: mode,
          tls_pin_sha256: mode === 'pin' ? probe?.sha256 : null,
          tls_server_name: mode === 'ca' ? serverName.trim() || null : null,
        });
        setLogin(result);
        if (!result.ok) throw new Invalid(result.error ?? t('Вход не удался.'));
        if (!commonName && result.suggested_common_names.length) setCommonName(result.suggested_common_names[0] ?? '');
        setStep(3);
      });
      return;
    }

    void guard(async () => {
      const portList = splitList(ports).map(Number);
      if (portList.some(p => !Number.isInteger(p) || p < 1 || p > 65535)) throw new Invalid(t('Порты — числа от 1 до 65535 через запятую.'));
      const created = await api.post<KerioServer>('/api/kerio/servers', {
        name: name.trim(),
        base_url: normalizedUrl(),
        username: username.trim(),
        password,
        tls_mode: mode,
        tls_pin_sha256: mode === 'pin' ? probe?.sha256 : null,
        tls_server_name: mode === 'ca' ? serverName.trim() || null : null,
        common_name: commonName || null,
        auto_deploy: auto,
        cleanup_old: cleanup,
        verify_ports: portList,
      });
      onClose(created.id);
    });
  };

  const names = [...new Set([...(login?.suggested_common_names ?? []), ...kmp])];

  return (
    <dialog ref={ref} className="wide wizard" onCancel={e => { e.preventDefault(); onClose(null); }}>
      <form noValidate onSubmit={next}>
        <div>
          <h2>{t('Подключить Kerio Connect')}</h2>
          <ol className="wizard-steps">
            {[t('Адрес'), t('Учётная запись'), t('Сертификат')].map((title, i) => (
              <li key={title} className={step === i + 1 ? 'current' : step > i + 1 ? 'done' : ''}>
                <span>{step > i + 1 ? <Icon name="check" /> : i + 1}</span>{title}
              </li>
            ))}
          </ol>
        </div>

        {step === 1 && (
          <>
            <Field label={t('Имя сервера')} hint={t('Так он будет называться в панели и письмах')}>
              <input type="text" autoFocus placeholder={t('Почта — главный офис')} value={name} onChange={e => setName(e.target.value)} />
            </Field>
            <Field label={t('Адрес администрирования')} hint={t('Адрес веб-администрирования Kerio, обычно порт 4040')}>
              <input type="text" inputMode="url" placeholder="https://mail.example.com:4040" value={url} onChange={e => setUrl(e.target.value)} />
            </Field>
            <p className="muted small">{t('Сначала сервис посмотрит, какой сертификат подаёт этот адрес, — до того, как туда уйдёт пароль.')}</p>
          </>
        )}

        {step === 2 && probe && (
          <>
            <ProbeBox probe={probe} />
            <TrustChoice probe={probe} mode={mode} onMode={setMode} serverName={serverName} onServerName={setServerName}
              pinHint={t('Доверять ровно этому сертификату. После установки нового сервис перезакрепит его сам.')} />
            <div className="row">
              <Field label={t('Логин Kerio')} hint={t('Отдельная учётная запись с правами полного администратора, без второго фактора')}>
                <input type="text" autoComplete="off" autoFocus value={username} onChange={e => setUsername(e.target.value)} />
              </Field>
              <Field label={t('Пароль')}>
                <input type="password" autoComplete="new-password" value={password} onChange={e => setPassword(e.target.value)} />
              </Field>
            </div>
          </>
        )}

        {step === 3 && login && (
          <>
            <div className="probe ok">
              <div className="probe-head">
                <Icon name="check" />
                <div>
                  <b>{t('Вход выполнен{0}', login.version && ' · Kerio Connect ' + login.version)}</b>
                  <span className="muted small">{t('{0} · {1} · сертификатов в хранилище: {2}', login.login_name, login.role, login.certificates.length)}</span>
                </div>
              </div>
            </div>
            <Field label={t('Сертификат из Key Manager Plus')} hint={t('Его новые выпуски будут ставиться на этот сервер; поменять можно карандашом в списке')}>
              <select value={commonName} onChange={e => setCommonName(e.target.value)}>
                <option value="">{t('Не выбирать — только наблюдение за сроком')}</option>
                {names.map(n => <option key={n} value={n}>{n}{login.suggested_common_names[0] === n ? t(' — стоит на сервере сейчас') : login.suggested_common_names.includes(n) ? t(' — покрывает имя на сервере') : ''}</option>)}
              </select>
            </Field>
            <Field label={t('Порты для проверки после установки')} hint={t('Найдены по тому же сертификату: новый должен появиться на каждом, иначе вернётся прежний')}>
              <input type="text" value={ports} onChange={e => setPorts(e.target.value)} />
            </Field>
            <Check label={t('Ставить продлённый сертификат автоматически')} checked={auto} onChange={setAuto} />
            <Check label={t('Убирать свои старые версии из хранилища Kerio (предыдущая остаётся, чужие не трогаются)')} checked={cleanup} onChange={setCleanup} />
            <p className="muted small">{t('Кому писать о событиях сервера — во вкладке «Оповещения».')}</p>
          </>
        )}

        <ErrorText text={error} />
        <div className="actions-row">
          {step > 1 && <button type="button" onClick={() => { setError(''); setStep((step - 1) as Step); }}>{t('Назад')}</button>}
          <button type="button" onClick={() => onClose(null)}>{t('Отмена')}</button>
          <button type="submit" className="primary" disabled={busy}>
            {busy ? t('Подождите…') : step === 1 ? t('Проверить адрес') : step === 2 ? t('Проверить вход') : t('Подключить')}
          </button>
        </div>
      </form>
    </dialog>
  );
}

function ProbeBox({ probe, warn, children }: { probe: Probe; warn?: boolean; children?: ReactNode }) {
  return (
    <div className={'probe' + (warn ? ' warn' : '')}>
      <div className="probe-head">
        <Icon name={warn ? 'alert' : 'shield'} />
        <div>
          <b className="mono">{probe.subject}</b>
          <span className="muted small">{t('выдан {0} · до {1}{2}', probe.issuer, fmtDate(probe.not_after.slice(0, 10)), probe.self_signed && t(' · самоподписанный'))}</span>
        </div>
      </div>
      <div className="mono small fp">SHA-256 {fingerprint(probe.sha256)}</div>
      {children}
    </div>
  );
}

function TrustChoice({ probe, mode, onMode, serverName, onServerName, pinHint }: {
  probe: Probe;
  mode: 'ca' | 'pin';
  onMode: (mode: 'ca' | 'pin') => void;
  serverName: string;
  onServerName: (name: string) => void;
  pinHint: string;
}) {
  return (
    <>
      <div className="choices two">
        <label className={mode === 'pin' ? 'checked' : ''}>
          <input type="radio" name="trust" checked={mode === 'pin'} onChange={() => onMode('pin')} />
          <b>{t('Закрепить отпечаток')}</b>
          <span>{pinHint}</span>
        </label>
        <label className={mode === 'ca' ? 'checked' : ''}>
          <input type="radio" name="trust" checked={mode === 'ca'} onChange={() => onMode('ca')} />
          <b>{t('Центр сертификации')}</b>
          <span>{probe.chain_trusted ? t('Цепочка доверенная: проверять её и имя.') : t('Цепочка сейчас не доверенная — вход не пройдёт.')}</span>
        </label>
      </div>
      {mode === 'ca' && (
        <Field label={t('Имя в сертификате')} hint={t('Если адрес — IP или другое имя, укажите имя из сертификата')}>
          <input type="text" placeholder={probe.dns_names[0] ?? 'mail.example.com'} value={serverName} onChange={e => onServerName(e.target.value)} />
        </Field>
      )}
    </>
  );
}

/** Перепроверить сертификат порта администрирования и подтвердить доверие; закрывается обновлённым сервером. */
function TrustDialog({ server: s, onClose }: { server: KerioServer; onClose: (updated: KerioServer | null) => void }) {
  const [probe, setProbe] = useState<Probe | null>(null);
  const [error, setError] = useState('');
  const [mode, setMode] = useState<'ca' | 'pin'>(s.tls_mode);
  const [serverName, setServerName] = useState(s.tls_server_name ?? '');
  const result = useRef<KerioServer | null>(null);

  useEffect(() => {
    api.post<Probe>('/api/kerio/servers/probe', { base_url: s.base_url }).then(setProbe, err => setError(errorText(err)));
  }, [s.base_url]);

  const changed = probe && s.tls_mode === 'pin' && probe.sha256 !== s.tls_pin_sha256;

  return (
    <FormModal
      title={t('Доверие к сертификату Kerio')}
      note={t('По этому соединению уходит пароль администратора Kerio, поэтому отключить проверку нельзя.')}
      wide
      submit={mode === 'pin' ? t('Доверять этому сертификату') : t('Сохранить')}
      onSubmit={async () => {
        if (!probe) throw new Invalid(error || t('Сервер не ответил по TLS.'));
        result.current = await api.put<KerioServer>(`/api/kerio/servers/${s.id}/trust`, {
          tls_mode: mode,
          tls_pin_sha256: mode === 'pin' ? probe.sha256 : null,
          tls_server_name: mode === 'ca' ? serverName.trim() || null : null,
        });
      }}
      onClose={done => onClose(done ? result.current : null)}
    >
      {error && <p className="note">{error}</p>}
      {!probe && !error && <Spinner />}
      {probe && (
        <>
          <ProbeBox probe={probe} warn={Boolean(changed)}>
            {changed && <p className="small">{t('Сервер подаёт не тот сертификат, что закреплён ({0}). Подтвердите, только если сменили его намеренно.', shortFingerprint(s.tls_pin_sha256))}</p>}
          </ProbeBox>
          <TrustChoice probe={probe} mode={mode} onMode={setMode} serverName={serverName} onServerName={setServerName}
            pinHint={t('Доверять ровно сертификату выше.')} />
        </>
      )}
    </FormModal>
  );
}
