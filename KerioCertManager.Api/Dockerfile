# Контекст сборки — корень репозитория.

# ⚠ Именно `-extra`: в нём ICU и tzdata; в образе нет оболочки — `RUN` в последней стадии невозможен.
# ⚠ Собирать с `--pull`, иначе базовый образ возьмётся из кэша со старыми уязвимостями.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:9b755ea799ae649141a073e30d8b62d8095a5465af2b7694d17866e8c05131d8 AS base
WORKDIR /app
EXPOSE 8001

# Веб-панель; манифесты отдельно — слой `npm ci` кэшируется.
FROM node:24-alpine@sha256:8a32c8cf649a49915a66e81e7a609223c708d0adabf288ee3f4077d765542f85 AS web
WORKDIR /web
COPY ["KerioCertManager.Web/package.json", "KerioCertManager.Web/package-lock.json", "./"]
RUN npm ci --no-audit --no-fund --ignore-scripts
COPY ["KerioCertManager.Web/", "./"]
RUN PANEL_OUT_DIR=/web/dist npm run build

FROM mcr.microsoft.com/dotnet/sdk:11.0@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
ARG BUILD_CONFIGURATION=Release
# Тег релиза без «v»; пусто — версия из Directory.Build.props.
ARG VERSION=
WORKDIR /src

# Сначала файлы проектов — слой restore кэшируется. Модули `modules/` — целиком: это копии с закреплённой версией.
# ⚠ Каждый свой проект — строкой COPY ниже, иначе publish --no-restore падает.
COPY ["Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["modules/", "modules/"]
COPY ["KerioCertManager.Api/KerioCertManager.Api.csproj", "KerioCertManager.Api/"]
COPY ["KerioCertManager.Modules.KeyManager/KerioCertManager.Modules.KeyManager.csproj", "KerioCertManager.Modules.KeyManager/"]
COPY ["KerioCertManager.Modules.Kerio/KerioCertManager.Modules.Kerio.csproj", "KerioCertManager.Modules.Kerio/"]
RUN dotnet restore "KerioCertManager.Api/KerioCertManager.Api.csproj"

COPY . .
RUN dotnet publish "KerioCertManager.Api/KerioCertManager.Api.csproj" \
    -c "$BUILD_CONFIGURATION" -o /publish --no-restore /p:UseAppHost=false /p:PublishDocumentationFile=false /p:PublishReferencesDocumentationFiles=false /p:DebugType=none /p:DebugSymbols=false ${VERSION:+/p:Version=$VERSION}

FROM base AS final
WORKDIR /app
COPY --from=build /publish .
COPY --from=web /web/dist ./wwwroot
# Без отладчика, профилировщика и диагностических сокетов.
ENV DOTNET_EnableDiagnostics=0
ENV ASPNETCORE_HTTP_PORTS=8001
# Непривилегированный пользователь.
USER $APP_UID
ENTRYPOINT ["dotnet", "KerioCertManager.Api.dll"]
