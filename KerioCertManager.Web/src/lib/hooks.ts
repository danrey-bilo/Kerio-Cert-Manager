import { useCallback, useEffect, useRef, useState } from 'react';
import { Cancelled, errorText } from './api';

/** Данные страницы: загрузка, ошибка и перезагрузка после действий. Неудачный опрос прежние данные не стирает. */
export function useLoad<T>(load: () => Promise<T>) {
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
  }, [version]);

  const reload = useCallback(() => setVersion(v => v + 1), []);
  /** Для опроса: пока прошлый запрос не ответил, новый не идёт. */
  const refresh = useCallback(() => { if (!loading.current) setVersion(v => v + 1); }, []);

  return { data, error, reload, refresh };
}

/**
 * Постраничный список: смена <c>key</c> — первая страница заново, ответ устаревшего запроса отбрасывается.
 * Ошибка догрузки список не стирает. <c>refresh</c> освежает начало списка, уже догруженное остаётся.
 */
export function usePagedList<T>(
  fetchPage: (cursor: string | null) => Promise<{ items: T[]; next: string | null }>,
  idOf: (item: T) => string | number,
  key: unknown = null,
) {
  const [items, setItems] = useState<T[] | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const source = useRef({ fetchPage, idOf });
  source.current = { fetchPage, idOf };
  const turn = useRef(0);
  const busy = useRef(false);
  const loaded = useRef(false);

  const load = useCallback(async (asked: 'first' | 'more' | 'top', cursor: string | null = null) => {
    // Начала ещё нет — освежать нечего: это первая загрузка.
    const mode = asked === 'top' && !loaded.current ? 'first' : asked;
    const mine = ++turn.current;
    busy.current = true;
    setLoading(true);
    try {
      const page = await source.current.fetchPage(cursor);
      if (mine !== turn.current) return;
      setItems(list => {
        if (mode === 'more') return [...(list ?? []), ...page.items];
        if (mode === 'first') return page.items;
        const fresh = new Set(page.items.map(source.current.idOf));
        return [...page.items, ...(list ?? []).filter(item => !fresh.has(source.current.idOf(item)))];
      });
      if (mode !== 'top') setNext(page.next);
      loaded.current = true;
      setError('');
    } catch (err) {
      if (mine === turn.current && !(err instanceof Cancelled)) {
        setError(errorText(err));
        // ⚠ Смена фильтра не удалась: строки и курсор прежнего фильтра под новым — неправда.
        if (mode === 'first') {
          loaded.current = false;
          setItems(null);
          setNext(null);
        }
      }
    } finally {
      if (mine === turn.current) {
        busy.current = false;
        setLoading(false);
      }
    }
  }, []);

  useEffect(() => { void load('first'); }, [key, load]);

  const refresh = useCallback(() => { if (!busy.current) void load('top'); }, [load]);
  const loadMore = () => { if (next) void load('more', next); };

  return { items, error, loading, hasMore: next !== null, loadMore, refresh };
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
