# The relay, as one container. Build from spikes/ (the relay links S2's channel code):
#   docker build -f s4-relay/Dockerfile -t agent-bridge-relay-s4 .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY s2-noise-interop/dotnet/Noise.cs s2-noise-interop/dotnet/
COPY s2-noise-interop/web/noise-p256.js s2-noise-interop/web/
COPY s4-relay/src/S4.Protocol s4-relay/src/S4.Protocol
COPY s4-relay/src/S4.Relay s4-relay/src/S4.Relay
COPY s4-relay/web s4-relay/web
RUN dotnet publish s4-relay/src/S4.Relay -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The image's built-in unprivileged user; ASP.NET Core images listen on 8080 by default.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "S4.Relay.dll"]
