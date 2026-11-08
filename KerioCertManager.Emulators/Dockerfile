# Демо-стенд: эмулятор Key Manager Plus и серверов Kerio Connect. Контекст сборки — корень репозитория.
# ⚠ Не для боя: учебный УЦ, известные пароли.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY ["Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["KerioCertManager.Emulators/", "KerioCertManager.Emulators/"]
RUN dotnet publish "KerioCertManager.Emulators/KerioCertManager.Emulators.csproj" -c Release -o /publish /p:UseAppHost=false

# Не chiseled: эмулятору нужен системный пакет корней, чтобы собрать пакет доверия для сервиса.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:1a48394b6f29d3a32c79d9edecd4abda0ecbe8776d567dbfc4590ed6979c740c
WORKDIR /app
COPY --from=build /publish .
ENV EMULATOR_DATA=/data
RUN mkdir -p /data && chown "$APP_UID" /data
VOLUME ["/data"]
USER $APP_UID
EXPOSE 8080 6565 4041 4042 993 995
ENTRYPOINT ["dotnet", "KerioCertManager.Emulators.dll"]
