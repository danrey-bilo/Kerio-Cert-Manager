# Демо-стенд: эмулятор Key Manager Plus и серверов Kerio Connect. Контекст сборки — корень репозитория.
# ⚠ Не для боя: учебный УЦ, известные пароли.
FROM mcr.microsoft.com/dotnet/sdk:11.0@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /src
COPY ["Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["KerioCertManager.Emulators/", "KerioCertManager.Emulators/"]
RUN dotnet publish "KerioCertManager.Emulators/KerioCertManager.Emulators.csproj" -c Release -o /publish /p:UseAppHost=false

# Не chiseled: эмулятору нужен системный пакет корней, чтобы собрать пакет доверия для сервиса.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
WORKDIR /app
COPY --from=build /publish .
ENV EMULATOR_DATA=/data
RUN mkdir -p /data && chown "$APP_UID" /data
VOLUME ["/data"]
USER $APP_UID
EXPOSE 8080 6565 4041 4042 993 995
ENTRYPOINT ["dotnet", "KerioCertManager.Emulators.dll"]
