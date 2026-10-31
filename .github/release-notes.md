Образ / Image: [`{IMAGE}:{VERSION}`](https://hub.docker.com/r/{IMAGE}) (Docker Hub, открытый / public)

## Установка через Portainer

1. Скачайте **`kerio-cert-manager-{TAG}.zip`** и распакуйте. В нём: `docker-compose.yml`,
   `.env.example` и `README.md` с подробностями; образ стек скачает из Docker Hub сам
   (без интернета — **Images → Import** → `kerio-cert-manager-image-{TAG}.tar`).
2. Образ закреплён на `{IMAGE}:{VERSION}`.
3. Заполните `.env.example`: мастер-ключ, два пароля базы, токен Cloudflare Tunnel, первый
   администратор (команды генерации — в самом файле).
4. **Stacks → Add stack** → **Upload** → `docker-compose.yml` → **Load variables from .env file**
   → **Deploy the stack**.
5. В Cloudflare у туннеля: Public Hostname → `HTTP` `api:8001`.
6. В панели: ключ API Key Manager Plus, затем **Серверы → Подключить сервер** для каждого Kerio Connect.

## Обновление

В переменных стека `KCM_IMAGE={IMAGE}:{VERSION}` → **Update the stack**. Миграции выполнятся сами.

## Командная строка

`kerio-cert-manager-cli-{TAG}.zip` — вариант с секретами в файлах:
[инструкция](https://github.com/{REPOSITORY}/blob/main/README.ru.md#установка-из-командной-строки).

## In English

- **Portainer:** download **`kerio-cert-manager-{TAG}.zip`**, fill in `.env.example`, then **Stacks → Add stack →
  Upload** the `docker-compose.yml` with **Load variables from .env file**; the image is pulled from Docker Hub. In
  Cloudflare, point the tunnel's Public Hostname to `HTTP` `api:8001`.
- **Update:** set `KCM_IMAGE={IMAGE}:{VERSION}` in the stack variables, **Update the stack** — migrations run by
  themselves.
- **Command line:** `kerio-cert-manager-cli-{TAG}.zip` keeps secrets in files —
  [instructions](https://github.com/{REPOSITORY}#installation-from-the-command-line).
- Free to use, no code copying — see [LICENSE](https://github.com/{REPOSITORY}/blob/main/LICENSE).

Контрольные суммы / Checksums — `SHA256SUMS`.
