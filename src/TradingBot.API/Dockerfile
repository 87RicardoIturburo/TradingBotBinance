FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copiar csprojs y restaurar (capa de caché para dependencias)
COPY src/TradingBot.Core/TradingBot.Core.csproj src/TradingBot.Core/
COPY src/TradingBot.Application/TradingBot.Application.csproj src/TradingBot.Application/
COPY src/TradingBot.Infrastructure/TradingBot.Infrastructure.csproj src/TradingBot.Infrastructure/
COPY src/TradingBot.API/TradingBot.API.csproj src/TradingBot.API/
RUN dotnet restore src/TradingBot.API/TradingBot.API.csproj

# Copiar código fuente y publicar
COPY src/ src/
RUN dotnet publish src/TradingBot.API/TradingBot.API.csproj -c Release -o /app --no-restore

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app

# Crear directorio de logs
RUN mkdir -p /app/logs

# Copiar binarios publicados
COPY --from=build /app .

# Variables de entorno por defecto (seguras)
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV BINANCE_USE_TESTNET=true

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "TradingBot.API.dll"]
