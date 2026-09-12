# TradingScanner: API plus the dashboard, one image. Build context: repository root.
# The dashboard is built as a static export and served by the API from its own origin, so a single
# web service (Render, Fly, a VM) is the whole deployment: no CORS, no second service, no API URL to wire.

FROM node:22-alpine AS web
WORKDIR /web
RUN corepack enable
COPY web/package.json web/pnpm-lock.yaml web/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY web/ ./
ENV NEXT_OUTPUT=export
ENV NEXT_TELEMETRY_DISABLED=1
RUN pnpm build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props TradingScanner.sln ./
COPY src/ src/
RUN dotnet restore src/TradingScanner.Api/TradingScanner.Api.csproj
RUN dotnet publish src/TradingScanner.Api/TradingScanner.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
COPY --from=web /web/out ./wwwroot
ENV ASPNETCORE_ENVIRONMENT=Production
# Render (and most PaaS) inject PORT; Program.cs binds to it when present, otherwise appsettings Urls applies.
EXPOSE 5080
ENTRYPOINT ["dotnet", "TradingScanner.Api.dll"]
