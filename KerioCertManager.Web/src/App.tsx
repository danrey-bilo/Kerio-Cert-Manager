import { lazy, Suspense, useCallback, useEffect, useRef, useState } from 'react';
import { api, Cancelled, connect, errorText, forget, refresh } from './lib/api';
import { isMobile } from './lib/device';
import { applyLocale, isLocale, locale, LOCALE_SETTING, t } from './lib/i18n';
import { useHashRoute } from './lib/hooks';
import type { Me, SecondFactorChallenge } from './lib/types';
import { Enrollment, Login, PasswordChange, SecondFactor } from './pages/Auth';
import { LinkPage } from './pages/LinkPage';
import { OverviewPage } from './pages/Overview';
import { Spinner } from './ui/bits';
import { Brand, Icon, type IconName } from './ui/Icon';
import { Menu } from './ui/Menu';
import { OverlayProvider, useOverlay } from './ui/Overlay';
import { StepUpDialog } from './ui/StepUp';

const AlertsPage = lazy(() => import('./pages/Alerts').then(m => ({ default: m.AlertsPage })));
const AuditPage = lazy(() => import('./pages/Audit').then(m => ({ default: m.AuditPage })));
const CertificatesPage = lazy(() => import('./pages/Certificates').then(m => ({ default: m.CertificatesPage })));
const JournalPage = lazy(() => import('./pages/Journal').then(m => ({ default: m.JournalPage })));
const ProfilePage = lazy(() => import('./pages/Profile').then(m => ({ default: m.ProfilePage })));
const SecurityPage = lazy(() => import('./pages/Security').then(m => ({ default: m.SecurityPage })));
const ServersPage = lazy(() => import('./pages/Servers').then(m => ({ default: m.ServersPage })));
const SettingsPage = lazy(() => import('./pages/Settings').then(m => ({ default: m.SettingsPage })));
const UsersPage = lazy(() => import('./pages/Users').then(m => ({ default: m.UsersPage })));

type Screen =
  | { kind: 'loading' }
  | { kind: 'login'; message?: string }
  | { kind: 'second'; challenge: SecondFactorChallenge }
  | { kind: 'enroll' }
  | { kind: 'password' }
  | { kind: 'app'; me: Me };

/** Основные разделы — в шапке на ПК и в нижней панели на телефоне. */
const NAV: Array<[key: string, title: string, icon: IconName]> = [
  ['overview', t('Обзор'), 'grid'],
  ['servers', t('Серверы'), 'server'],
  ['certificates', t('Сертификаты'), 'cert'],
  ['journal', t('Журнал'), 'history'],
  ['alerts', t('Оповещения'), 'bell'],
  ['settings', t('Настройки'), 'settings'],
];

/** Администрирование — в меню пользователя. */
const MORE: Array<[key: string, title: string, icon: IconName]> = [
  ['users', t('Пользователи'), 'users'],
  ['audit', t('Журнал действий'), 'shield'],
  ['security', t('Безопасность'), 'lock'],
  ['profile', t('Профиль'), 'key'],
];

const TITLES: Record<string, string> = Object.fromEntries([...NAV, ...MORE].map(([k, t]) => [k, t]));

/** Старые адреса разделов настроек. */
const MOVED: Record<string, string> = { mail: '#/settings/mail', keymanager: '#/settings/keymanager' };

export function App() {
  if (location.pathname === '/activate') return <OverlayProvider><LinkPage kind="activate" /></OverlayProvider>;
  if (location.pathname === '/password-reset') return <OverlayProvider><LinkPage kind="reset" /></OverlayProvider>;

  return <OverlayProvider><Panel /></OverlayProvider>;
}

function Panel() {
  const [screen, setScreen] = useState<Screen>({ kind: 'loading' });
  const { show } = useOverlay();

  /** Настройка фактора и смена пароля — раньше всего. */
  const enter = useCallback(async () => {
    try {
      const me = await api.get<Me>('/api/me');
      const prefs = await api.get<{ effective: Record<string, unknown> }>('/api/me/preferences').catch(() => null);
      const chosen = prefs?.effective[LOCALE_SETTING];
      if (isLocale(chosen) && chosen !== locale()) applyLocale(chosen);
      if (me.must_enroll_two_factor) return setScreen({ kind: 'enroll' });
      if (me.must_change_password) return setScreen({ kind: 'password' });
      if (!location.hash.startsWith('#/')) history.replaceState(null, '', '#/overview');
      setScreen({ kind: 'app', me });
    } catch (err) {
      if (!(err instanceof Cancelled)) setScreen({ kind: 'login', message: errorText(err) });
    }
  }, []);

  const logout = useCallback(async () => {
    await api.post('/api/auth/logout').catch(() => null);
    forget();
    setScreen({ kind: 'login' });
  }, []);

  useEffect(() => {
    connect({
      signedOut: message => setScreen({ kind: 'login', message }),
      confirmIdentity: minutes => show(close => <StepUpDialog minutes={minutes} onClose={close} />),
    });
    void refresh().then(ok => (ok ? enter() : setScreen({ kind: 'login' })));
  }, [enter, show]);

  switch (screen.kind) {
    case 'loading':
      return <Spinner />;
    case 'login':
      return <Login message={screen.message} onEntered={enter} onSecondFactor={challenge => setScreen({ kind: 'second', challenge })} />;
    case 'second':
      return <SecondFactor challenge={screen.challenge} onEntered={enter} onBack={message => setScreen({ kind: 'login', message })} />;
    case 'enroll':
      return <Enrollment onDone={enter} onLogout={logout} />;
    case 'password':
      return <PasswordChange onDone={enter} onLogout={logout} />;
    case 'app':
      return <Shell me={screen.me} onRenamed={enter} onLogout={logout} />;
  }
}

function initials(me: Me): string {
  const source = (me.display_name || me.email).trim();
  const parts = source.split(/[\s@._-]+/).filter(Boolean);
  return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || '?';
}

function Shell({ me, onRenamed, onLogout }: { me: Me; onRenamed: () => void; onLogout: () => void }) {
  const route = useHashRoute();
  const [first = '', second] = route;

  useEffect(() => {
    if (MOVED[first]) history.replaceState(null, '', MOVED[first]);
  }, [first]);

  const page = MOVED[first] ? 'settings' : first in TITLES ? first : 'overview';
  const sub = MOVED[first] ? first : second;

  useEffect(() => { document.title = TITLES[page] + ' · Kerio Cert Manager'; }, [page]);

  const content = (
    <main key={page}>
      <Suspense fallback={<Spinner />}>
        {page === 'overview' && <OverviewPage />}
        {page === 'servers' && <ServersPage key={sub ?? 'list'} id={sub} />}
        {page === 'certificates' && <CertificatesPage />}
        {page === 'journal' && <JournalPage />}
        {page === 'alerts' && <AlertsPage />}
        {page === 'users' && <UsersPage myId={me.id} />}
        {page === 'settings' && <SettingsPage section={sub} myEmail={me.email} />}
        {page === 'security' && <SecurityPage />}
        {page === 'audit' && <AuditPage />}
        {page === 'profile' && <ProfilePage onRenamed={onRenamed} onLogout={onLogout} />}
      </Suspense>
    </main>
  );

  const avatar = (
    <a href="#/profile" className={'avatar' + (page === 'profile' ? ' active' : '')} title={me.email} aria-label={t('Профиль')}>
      {initials(me)}
    </a>
  );

  if (isMobile) {
    const inMore = MORE.some(([key]) => key === page);
    return (
      <>
        <header className="top">
          <Brand />
          {avatar}
        </header>
        {content}
        <nav className="tabbar" aria-label={t('Разделы')}>
          {NAV.slice(0, 4).map(([key, title, icon]) => (
            <a key={key} href={'#/' + key} className={key === page ? 'active' : ''} aria-current={key === page ? 'page' : undefined}>
              <Icon name={icon} />
              <span>{title}</span>
            </a>
          ))}
          <MoreTab active={inMore || page === 'settings' || page === 'alerts'} onLogout={onLogout} />
        </nav>
      </>
    );
  }

  return (
    <div className="shell">
      <header className="top">
        <Brand />
        <nav className="nav" aria-label={t('Разделы')}>
          {NAV.map(([key, title, icon]) => (
            <a key={key} href={'#/' + key} className={key === page ? 'active' : ''} aria-current={key === page ? 'page' : undefined}>
              <Icon name={icon} />
              {title}
            </a>
          ))}
        </nav>
        <div className="who">
          <a href="#/profile" className={'who-name' + (page === 'profile' ? ' active' : '')} title={me.email}>
            <span className="avatar small">{initials(me)}</span>
            <span>{me.display_name || me.email}</span>
          </a>
          <Menu
            items={[
              { text: t('Пользователи'), action: () => { location.hash = '#/users'; } },
              { text: t('Журнал действий'), action: () => { location.hash = '#/audit'; } },
              { text: t('Безопасность'), action: () => { location.hash = '#/security'; } },
              { text: t('Профиль'), action: () => { location.hash = '#/profile'; } },
              { text: t('Выйти'), action: onLogout, danger: true },
            ]}
          />
        </div>
      </header>
      {content}
    </div>
  );
}

/** Вкладка «Ещё» на телефоне: настройки и администрирование листом снизу. */
function MoreTab({ active, onLogout }: { active: boolean; onLogout: () => void }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  }, [open]);

  const go = (hash: string) => { setOpen(false); location.hash = hash; };

  return (
    <>
      <button type="button" className={'tab-more' + (active ? ' active' : '')} onClick={() => setOpen(true)} aria-haspopup="dialog">
        <Icon name="menu" />
        <span>{t('Ещё')}</span>
      </button>
      <dialog
        ref={ref}
        className="sheet"
        aria-label={t('Ещё')}
        onCancel={e => { e.preventDefault(); setOpen(false); }}
        onClick={e => { if (e.target === e.currentTarget) setOpen(false); }}
      >
        {open && (
          <div className="sheet-body">
            <div className="sheet-list">
              <button type="button" onClick={() => go('#/alerts')}><Icon name="bell" />{t('Оповещения')}</button>
              <button type="button" onClick={() => go('#/settings')}><Icon name="settings" />{t('Настройки')}</button>
              {MORE.map(([key, title, icon]) => (
                <button key={key} type="button" onClick={() => go('#/' + key)}><Icon name={icon} />{title}</button>
              ))}
            </div>
            <div className="sheet-list">
              <button type="button" className="danger" onClick={() => { setOpen(false); onLogout(); }}><Icon name="logout" />{t('Выйти')}</button>
            </div>
            <button type="button" className="sheet-cancel" onClick={() => setOpen(false)}>{t('Закрыть')}</button>
          </div>
        )}
      </dialog>
    </>
  );
}
