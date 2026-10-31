import { useState } from 'react';
import { api } from '../lib/api';
import { t } from '../lib/i18n';
import { CodeInput } from './bits';
import { FormModal } from './Modal';

/** Окно свежего кода: опасное действие повторится само, когда код подойдёт. */
export function StepUpDialog({ minutes, onClose }: { minutes?: number; onClose: (done: boolean) => void }) {
  const [code, setCode] = useState('');

  return (
    <FormModal
      title={t('Подтвердите кодом')}
      note={t('Код из приложения-аутентификатора. {0}', (minutes ? t('Следующие {0} мин спрашивать не будем.', minutes) : ''))}
      submit={t('Подтвердить')}
      onSubmit={() => api.stepUp(code)}
      onClose={onClose}
    >
      <CodeInput value={code} onChange={setCode} />
    </FormModal>
  );
}
