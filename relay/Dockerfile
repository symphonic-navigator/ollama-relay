# Build context: repository root.
#   docker build -f relay/Dockerfile -t ollama-relay .

FROM --platform=$BUILDPLATFORM node:22-alpine AS web
WORKDIR /src/relay/frontend
RUN corepack enable
COPY relay/frontend/package.json relay/frontend/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY relay/frontend/ ./
RUN pnpm build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY shared/ shared/
COPY relay/backend/ relay/backend/
RUN dotnet publish relay/backend/OllamaRelay.Relay/OllamaRelay.Relay.csproj -c Release -a $TARGETARCH -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
COPY --from=web /src/relay/frontend/dist ./wwwroot
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=3s CMD ["dotnet", "OllamaRelay.Relay.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "OllamaRelay.Relay.dll"]
