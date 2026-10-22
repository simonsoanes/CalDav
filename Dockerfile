# syntax=docker/dockerfile:1
# Nullify.CalDav server image. Build from the repository root:
#   docker build -t nullify-caldav .
# CI gate: `docker build --target test .` runs the test suite inside the SDK image.

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG VERSION=0.0.0
WORKDIR /src
COPY Nullify.CalDav.slnx ./
COPY src/Nullify.CalDav/Nullify.CalDav.csproj src/Nullify.CalDav/
COPY tests/Nullify.CalDav.Tests/Nullify.CalDav.Tests.csproj tests/Nullify.CalDav.Tests/
RUN dotnet restore Nullify.CalDav.slnx
COPY src/ src/
RUN dotnet publish src/Nullify.CalDav/Nullify.CalDav.csproj -c Release --no-restore -o /app \
      -p:UseAppHost=false -p:Version="$VERSION"

FROM build AS test
COPY tests/ tests/
RUN dotnet test tests/Nullify.CalDav.Tests -c Release --no-restore --logger "console;verbosity=normal"

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Storage__DataDirectory=/data \
    DOTNET_NOLOGO=true
# /data holds the SQLite database and the data protection keys that encrypt cookies and secrets.
RUN mkdir -p /data && chown "$APP_UID":"$APP_UID" /data
COPY --from=build /app .
USER $APP_UID
VOLUME ["/data"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nullify.CalDav.dll"]
