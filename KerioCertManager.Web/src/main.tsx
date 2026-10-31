import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { device } from './lib/device';
import { initLocale } from './lib/i18n';
import './styles.css';

document.documentElement.dataset.device = device;

// ⚠ Панель — после словаря: подписи разделов считаются при загрузке их модулей.
void initLocale().then(async () => {
  const { App } = await import('./App');
  createRoot(document.getElementById('root') as HTMLElement).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
});
