# Build context: repository root.
#   docker build -f node/Dockerfile -t ollama-relay-node .

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY shared/ shared/
COPY node/ node/
RUN dotnet publish node/OllamaRelay.Node/OllamaRelay.Node.csproj -c Release -a $TARGETARCH -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out ./
ENV DOTNET_NOLOGO=1
USER $APP_UID
ENTRYPOINT ["dotnet", "OllamaRelay.Node.dll"]
