# Security policy

**English** · [Русский](#политика-безопасности)

## Reporting a vulnerability

Report security problems directly to the owner of this repository, not in Issues or other shared places. Describe
what you found, how to reproduce it and which version you use. You will get an answer as soon as possible; a fix is
released as a new version, and the
[changelog](CHANGELOG.md) says what was fixed.

## Supported versions

Only the latest release receives fixes. Keep the service updated: new image, `migrate`, `up -d`
(see [README](README.md#updating)).

## What is in scope

The service itself: the web panel, the REST API, the handling of Kerio account passwords, the Key Manager Plus API
key, private keys of certificates, the SMTP password and e-mails. Problems in Key Manager Plus, Kerio Connect, your
mail server or Cloudflare should be reported to their vendors.

How the protection works is described in [docs/REFERENCE.md](docs/REFERENCE.md#безопасность) (in Russian) and in
the [README](README.md#security).

# Политика безопасности

[English](#security-policy) · **Русский**

## Как сообщить об уязвимости

О проблемах безопасности сообщайте напрямую владельцу этого репозитория, а не в Issues и не в общих каналах.
Опишите, что нашли, как это повторить и на какой версии. Ответ придёт как можно скорее; исправление выходит новой
версией, а в [CHANGELOG](CHANGELOG.md) написано, что исправлено.

## Какие версии исправляются

Только последний релиз. Обновляйте сервис: новый образ, `migrate`, `up -d` (см. [README](README.ru.md#обновление)).

## Что сюда относится

Сам сервис: веб-панель, REST API, обращение с паролями учётных записей Kerio, ключом API Key Manager Plus,
закрытыми ключами сертификатов, паролем SMTP и письмами. О проблемах Key Manager Plus, Kerio Connect, вашего
почтового сервера или Cloudflare сообщайте их производителям.

Устройство защиты — в [docs/REFERENCE.md](docs/REFERENCE.md#безопасность) и в [README](README.ru.md#безопасность).
