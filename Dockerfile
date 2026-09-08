FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Восстановление зависимостей
COPY ["BackupServer.Api/BackupServer.Api.csproj", "BackupServer.Api/"]
COPY ["BackupServer.Core/BackupServer.Core.csproj", "BackupServer.Core/"]
COPY ["BackupServer.Infrastructure/BackupServer.Infrastructure.csproj", "BackupServer.Infrastructure/"]
RUN dotnet restore "BackupServer.Api/BackupServer.Api.csproj"

# Сборка и публикация
COPY . .
WORKDIR "/src/BackupServer.Api"
RUN dotnet publish "BackupServer.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Финальный образ .NET 10 Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080

# Часовой пояс Казахстана
ENV TZ=Asia/Almaty
RUN apt-get update && apt-get install -y tzdata && \
    ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BackupServer.Api.dll"]