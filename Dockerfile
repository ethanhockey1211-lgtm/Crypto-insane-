# TradingScanner API. Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props TradingScanner.sln ./
COPY src/ src/
RUN dotnet restore src/TradingScanner.Api/TradingScanner.Api.csproj
RUN dotnet publish src/TradingScanner.Api/TradingScanner.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production
# Render (and most PaaS) inject PORT; Program.cs binds to it when present, otherwise appsettings Urls applies.
EXPOSE 5080
ENTRYPOINT ["dotnet", "TradingScanner.Api.dll"]
