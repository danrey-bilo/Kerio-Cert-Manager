# Установка через Portainer

В архиве релиза:

| Файл | Что это |
|---|---|
| `docker-compose.yml` | Стек: база, миграции, сервис, Cloudflare Tunnel |
| `.env.example` | Переменные стека с подсказками — секреты, токен туннеля, первый администратор |
| `kerio-cert-manager-image-vX.Y.Z.tar` | Образ сервиса — только для серверов без интернета |

## 1. Образ

Открытый образ в Docker Hub — [`danreero/kerio_cert_manager`](https://hub.docker.com/r/danreero/kerio_cert_manager):
стек скачает его сам. `KCM_IMAGE` в `.env.example` — закреплённая версия `danreero/kerio_cert_manager:X.Y.Z`.
Без интернета: **Portainer → Images → Import** → `kerio-cert-manager-image-vX.Y.Z.tar` → **Upload**.

## 2. Переменные

Откройте `.env.example` и заполните (команды генерации — в самом файле):

| Переменная | Что вписать |
|---|---|
| `KCM_MASTER_KEK` | мастер-ключ: `openssl rand -base64 32` |
| `POSTGRES_PASSWORD` | пароль владельца базы: `openssl rand -hex 24` |
| `KCM_APP_PASSWORD` | пароль роли сервиса — другой: `openssl rand -hex 24` |
| `CLOUDFLARE_TUNNEL_TOKEN` | токен туннеля из Cloudflare |
| `KCM_INIT_ADMIN_EMAIL`, `KCM_INIT_ADMIN_PASSWORD` | первый администратор |

> ⚠ Сохраните копию `KCM_MASTER_KEK` в надёжном месте. Без него не открыть пароли Kerio,
> ключ API Key Manager Plus, пароль SMTP и коды 2FA, даже если база цела.

Стек превращает эти переменные в секреты Docker: контейнеры читают их из файлов `/run/secrets`, в окружении
контейнеров и в `docker inspect` их нет. Доступ к Portainer — всё равно доступ к ним.

## 3. Стек

**Portainer → Stacks → Add stack**:

1. **Name:** `kerio-cert-manager`.
2. **Build method:** **Upload** → **Select a file** → `docker-compose.yml`.
3. **Environment variables:** **Load variables from .env file** → ваш заполненный файл.
4. **Deploy the stack**.

Порядок запуска сам: `postgres` → `migrate` (схема, роль базы, первый администратор; завершается
со статусом `exited (0)` — так и должно быть) → `api` → `cloudflared`.

## 4. Cloudflare

В туннеле: **Public Hostname → Add** — домен панели, **Service:** `HTTP`, **URL:** `api:8001`.
Включите **Always Use HTTPS** для домена и **Cloudflare Access** перед панелью (только сотрудники) —
[README, шаг 5](../../README.ru.md#установка-из-командной-строки).

## 5. Первый вход

В течение часа после развёртывания откройте адрес панели, войдите администратором из шага 2 и подключите
приложение-аутентификатор. Затем в стеке **очистите `KCM_INIT_ADMIN_PASSWORD`** → **Update the stack**.

## 6. Key Manager Plus и серверы Kerio

1. **Настройки → Key Manager Plus**: адрес (`https://kmp.example.com:6565`), ключ API
   (Key Manager Plus → Settings → General Settings → API Key) → «Сохранить». Статус станет «Работает».
2. **Серверы → Подключить сервер** — для каждого Kerio Connect: адрес администрирования
   (`https://mail.example.com:4040`), отдельная учётная запись с правами полного администратора, выбор
   сертификата из KMP. Мастер покажет сертификат сервера, проверит вход и сам найдёт порты служб.
3. Первые дни можно включить **Настройки → Синхронизация → Пробный режим**: всё проверяется, Kerio не
   меняется.

Контейнеру нужен исходящий доступ к KMP, к серверам Kerio (порт администрирования и порты служб) и к SMTP.
Если KMP подаёт сертификат внутреннего УЦ, положите корень в контейнер и задайте `SSL_CERT_FILE`.

## Обновление

1. В переменных стека — новый `KCM_IMAGE` (например `danreero/kerio_cert_manager:X.Y.Z`).
2. **Update the stack**. Образ скачается, миграции выполнятся сами (служба `migrate`).

Перед обновлением снимите копию базы (ниже).

## Восстановление доступа

Забыт пароль или потерян телефон: в переменных стека заполните `KCM_RESET_ACCESS_EMAIL` и
`KCM_RESET_ACCESS_PASSWORD` → **Update the stack**. Пароль станет новым, второй фактор,
блокировка и все сессии будут сняты. Сброс с этой парой «адрес + пароль» применяется **один раз**:
следующие обновления стека его не повторят. Понадобится снова — задайте другой пароль. **После входа
всё равно очистите оба поля и обновите стек**: пароль в переменных виден в Portainer и `docker inspect`.

## Сеть туннеля

API верит заголовкам `X-Forwarded-*` только от адреса cloudflared: `KCM_TUNNEL_PROXY_IP`
(`172.31.252.2`). Остальным Docker раздаёт адреса из `KCM_TUNNEL_RANGE` (`172.31.252.8/29`) — адрес
cloudflared в него не входит, и никто его не займёт. Сменили подсеть `KCM_TUNNEL_SUBNET` — задайте
внутри неё оба. У API закреплённого адреса нет: разовая команда стека при работающем API иначе падала бы
с «Address already in use».

## Копия базы

На сервере:

```bash
docker exec kerio-cert-manager-postgres-1 pg_dump -U kcm kcm > kcm.sql
```

Мастер-ключ храните отдельно от копий.

## Если что-то не так

- **`migrate` завершился с ошибкой** — Containers → `kerio-cert-manager-migrate-1` → Logs: там
  причина (не заполнена переменная, слабый пароль администратора, нет доступа к базе).
- **`api` не стартует** — Logs контейнера `api`; чаще всего не тот `KCM_APP_PASSWORD` или
  `KCM_MASTER_KEK`.
- **Туннель не поднимается** — Logs `cloudflared`: строка `Registered tunnel connection` значит,
  что всё в порядке.
