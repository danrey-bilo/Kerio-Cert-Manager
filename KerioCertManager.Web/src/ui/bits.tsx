import { useState, type ReactNode } from 'react';
import { t } from '../lib/i18n';
import { Icon, type IconName } from './Icon';

export type DotKind = '' | 'ok' | 'err' | 'warn';

const Dot = ({ kind = '' }: { kind?: DotKind }) => <span className={'dot ' + kind} />;

export function Status({ kind, children, strong }: { kind: DotKind; children: ReactNode; strong?: boolean }) {
  return <span className={'status' + (strong ? ' strong' : '')}><Dot kind={kind} />{children}</span>;
}

export const Spinner = () => <div className="spinner" role="status" aria-label={t('Загрузка')} />;

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="field">
      <span>{label}</span>
      {children}
      {hint && <small>{hint}</small>}
    </label>
  );
}

export function Check({ label, checked, onChange }: { label: string; checked: boolean; onChange: (value: boolean) => void }) {
  return (
    <label className="check">
      <input type="checkbox" checked={checked} onChange={e => onChange(e.target.checked)} />
      {label}
    </label>
  );
}

export function CodeInput({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  return (
    <input
      type="text"
      className="code"
      inputMode="numeric"
      autoComplete="one-time-code"
      maxLength={8}
      placeholder="000000"
      aria-label={t('Код')}
      required
      autoFocus
      value={value}
      onChange={e => onChange(e.target.value.replace(/\s/g, ''))}
    />
  );
}

/** Скопировать; без буфера (небезопасный контекст) — выделить. */
function useCopy(): [boolean, (text: string) => Promise<void>] {
  const [copied, setCopied] = useState(false);
  const copy = async (text: string) => {
    try { await navigator.clipboard.writeText(text); } catch { /* выделено — Ctrl+C */ }
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };
  return [copied, copy];
}

export function CopyField({ value }: { value: string }) {
  const [copied, copy] = useCopy();
  return (
    <div className="copy">
      <input type="text" readOnly value={value} onFocus={e => e.target.select()} />
      <button type="button" onClick={() => copy(value)}>
        <Icon name={copied ? 'check' : 'copy'} />{copied ? t('Скопировано') : t('Копировать')}
      </button>
    </div>
  );
}

export const ErrorText = ({ text }: { text: string }) => (text ? <p className="error" role="alert">{text}</p> : null);

export function PageHead({ title, sub, children }: { title: string; sub?: ReactNode; children?: ReactNode }) {
  return (
    <div className="head">
      <div className="head-text">
        <h1>{title}</h1>
        {sub && <p className="head-sub">{sub}</p>}
      </div>
      {children && <div className="head-actions">{children}</div>}
    </div>
  );
}

/** Переключатель разделов страницы ссылками на хэш — назад/вперёд браузера работают. */
export function Tabs({ items, value }: { items: Array<[href: string, title: string]>; value: string }) {
  return (
    <nav className="tabs" aria-label={t('Разделы')}>
      {items.map(([href, title]) => (
        <a key={href} href={href} className={href === value ? 'active' : ''} aria-current={href === value ? 'page' : undefined}>
          {title}
        </a>
      ))}
    </nav>
  );
}

export function Empty({ title, hint, children }: { title: string; hint?: string; children?: ReactNode }) {
  return (
    <div className="empty">
      <p className="empty-title">{title}</p>
      {hint && <p className="muted">{hint}</p>}
      {children}
    </div>
  );
}

export const Failed = ({ text }: { text: string }) => <div className="card"><p className="error">{text}</p></div>;

/** Опрос не удался, а на экране прошлые данные. */
export const StaleNotice = ({ error }: { error: string }) =>
  (error ? <p className="note">{t('Не удалось обновить данные: {0} На экране — последние полученные.', error)}</p> : null);

/** Переключатель настройки. */
export function Switch({ label, checked, onChange, title }: { label: string; checked: boolean; onChange: (value: boolean) => void; title?: string }) {
  return (
    <label className="switch" title={title}>
      <input type="checkbox" role="switch" checked={checked} aria-label={label} onChange={e => onChange(e.target.checked)} />
      <span />
    </label>
  );
}

/** Полоса над содержимым страницы: что важно сейчас и куда идти; своё действие — детьми. */
export function Banner({ tone = '', icon, title, text, href, action, children }: {
  tone?: '' | 'warn' | 'info' | 'err' | 'ok';
  icon: IconName;
  title: string;
  text: string;
  href?: string;
  action?: string;
  children?: ReactNode;
}) {
  return (
    <div className={'banner ' + tone}>
      <span className="banner-icon"><Icon name={icon} /></span>
      <div className="banner-text"><b>{title}</b><span>{text}</span></div>
      {href && action && <a className="button" href={href}>{action}</a>}
      {children}
    </div>
  );
}
