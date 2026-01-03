# 1CLauncher

Лаунчер для 1C с проверкой доменной авторизации через отдельный сервис `DCAuth`. Для запуска баз, опубликованных на web сервере с доменной авторизацией, например внешние пользователи, работающие с личных устройств.

Коротко
- `DCAuth` — минимальный HTTP-сервис (ASP.NET) проверяющий учётные данные в домене через LDAP и возвращающий JWT при успешной аутентификации.
- `1CLauncher` — клиентское приложение (Avalonia) с формой для ввода логина/пароля/домена и отправкой запроса к `DCAuth`.

Требования
- .NET SDK 8.0 (для `1CLauncher`)
- .NET SDK 10.0 (для `DCAuth`)
- Рекомендуется использовать `dotnet` CLI или Visual Studio 2022/2023 с поддержкой нужных таргетов.

Как собрать и запустить
1. Откройте терминал в корне репозитория.
2. Соберите сервис авторизации:
   - cd DCAuth
   - dotnet build
   - dotnet run
   Сервис по умолчанию читает `appsettings.Development.json` — убедитесь, что в нём настроен `DomainController:Url`.
3. Запустите клиентское приложение:
   - cd ../1CLauncher
   - dotnet build
   - dotnet run

Настройки
- DCAuth (`DCAuth/appsettings.Development.json`):
  - `DomainController:Url` — LDAP URL (например `ldap://dc.example.com:389` или `ldaps://dc.example.com`).
  - `DomainController:Domain` — (опционально) домен по умолчанию.
  - `Jwt:Key` — секрет HMAC для подписи токенов (в development в файле, в production — использовать секреты/KeyVault).
  - `Jwt:Issuer`, `Jwt:Audience`, `Jwt:ExpiresMinutes` — дополнительные параметры токена.

- 1CLauncher:
  - В поле ExternalUrl укажите базовый адрес сервиса `DCAuth`, например `http://localhost:5000` (в UI будет отправлен POST `/checkAuth`).
  - Настройки пользователя (Username, Domain, ExternalUrl) сохраняются в `%APPDATA%\\1CLauncher\\settings.json`.

API
- POST /checkAuth
  - Вход (JSON): `{ "username": "user", "password": "pwd", "domain": "MYDOMAIN" }`
  - Успех: `{ "authenticated": true, "token": "<jwt>" }`
  - Провал: `{ "authenticated": false }` или HTTP 500 при ошибках

Замечания по безопасности
- Не храните `Jwt:Key` в репозитории. Для production используйте защищённое хранилище ключей.
- TLS/LDAPS рекомендуется для защищённой передачи учётных данных.

Разработка
- Решение содержит два проекта: `DCAuth` и `1CLauncher`.
- Если при сборке клиента возникают ошибки дубликатов атрибутов сборки (duplicate assembly attributes), в проектном файле `1CLauncher.csproj` добавлены исключения для сгенерированных файлов в `obj`.

Лицензия
- Этот проект распространяется под лицензией GNU General Public License версии 3 (GNU GPL v3). Смотрите файл `LICENSE` в корне репозитория для полного текста лицензии.

- [<img src="https://infostart.ru/bitrix/templates/sandbox_empty/assets/tpl/abo/img/logo.svg">](https://github.com/Shooshpanius/1CLauncherForExtUsers)
