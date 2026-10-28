# The relay, as one container. Build from the repository root:
#   docker build -f src/AgentBridge.Relay/Dockerfile -t agent-bridge-relay .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/AgentBridge.Transport src/AgentBridge.Transport
COPY src/AgentBridge.Relay src/AgentBridge.Relay
RUN dotnet publish src/AgentBridge.Relay -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The image's built-in unprivileged user; ASP.NET Core images listen on 8080 by default.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgentBridge.Relay.dll"]
