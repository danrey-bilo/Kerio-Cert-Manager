import { useCallback, useEffect, useRef, useState } from 'react';
import { Cancelled, errorText } from './api';

/** Данные страницы: загрузка, ошибка и перезагрузка после действий. */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState('');
  const [version, setVersion] = useState(0);
  const loading = useRef(false);

  useEffect(() => {
    let alive = true;
    loading.current = true;
    load().then(
      value => { if (alive) { setData(value); setError(''); } },
      err => { if (alive && !(err instanceof Cancelled)) setError(errorText(err)); },
    ).finally(() => { if (alive) loading.current = false; });
    return () => { alive = false; loading.current = false; };
  }, [version, ...deps]);

  const reload = useCallback(() => setVersion(v => v + 1), []);
  /** Для опроса: пока прошлый запрос не ответил, новый не идёт. */
  const refresh = useCallback(() => { if (!loading.current) setVersion(v => v + 1); }, []);

  return { data, error, reload, refresh };
}

/** Опрос, пока вкладка видна; вернулись на вкладку — сразу свежие данные. */
export function usePolling(refresh: () => void, ms: number) {
  useEffect(() => {
    const visible = () => document.visibilityState === 'visible';
    const timer = setInterval(() => { if (visible()) refresh(); }, ms);
    const shown = () => { if (visible()) refresh(); };
    document.addEventListener('visibilitychange', shown);
    return () => {
      clearInterval(timer);
      document.removeEventListener('visibilitychange', shown);
    };
  }, [refresh, ms]);
}

/** Текущий маршрут панели из хэша: `#/settings/mail` → `['settings', 'mail']`. */
export function useHashRoute(): string[] {
  const read = () => location.hash.replace(/^#\//, '').split('/').filter(Boolean);
  const [route, setRoute] = useState(read);

  useEffect(() => {
    const changed = () => setRoute(read());
    window.addEventListener('hashchange', changed);
    return () => window.removeEventListener('hashchange', changed);
  }, []);

  return route;
}
