# The relay, as one container. Build from the repository root:
#   docker build -f src/AgentBridge.Relay/Dockerfile -t agent-bridge-relay .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/AgentBridge.Transport src/AgentBridge.Transport
COPY src/AgentBridge.Roster src/AgentBridge.Roster
COPY src/AgentBridge.Relay src/AgentBridge.Relay
RUN dotnet publish src/AgentBridge.Relay -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# CVE-2026-109518 (HIGH) is in Ubuntu 24.04's apt and libapt-pkg6.0t64 2.8.3, fixed in
# 2.8.4ubuntu0.1. The current aspnet:10.0 (built 2026-10-26) still ships 2.8.3, so a digest pin
# can't carry the fix yet: upgrade just those two in place, as root (the base's default user,
# before USER below), fail the build if the fixed version didn't arrive, and drop apt's lists.
# Remove this once an aspnet:10.0 ships 2.8.4ubuntu0.1 or later.
# apt retries a mirror that is mid-sync or slow (#1648: "File has unexpected size") instead of failing the build.
RUN apt-get -o Acquire::Retries=3 -o Acquire::http::Timeout=60 update \
 && apt-get -o Acquire::Retries=3 -o Acquire::http::Timeout=60 install -y --no-install-recommends --only-upgrade apt libapt-pkg6.0t64 \
 && dpkg --compare-versions "$(dpkg-query -W -f='${Version}' apt)" ge 2.8.4ubuntu0.1 \
 && dpkg --compare-versions "$(dpkg-query -W -f='${Version}' libapt-pkg6.0t64)" ge 2.8.4ubuntu0.1 \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# The image's built-in unprivileged user; ASP.NET Core images listen on 8080 by default.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgentBridge.Relay.dll"]
