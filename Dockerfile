# syntax=docker/dockerfile:1.7

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

ARG TARGETARCH

COPY CitusManager.csproj ./
RUN dotnet restore CitusManager.csproj --arch "${TARGETARCH}"

COPY . ./

ARG APP_VERSION=0.0.0-local
ARG SOURCE_REVISION=unknown
RUN dotnet publish CitusManager.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    --arch "${TARGETARCH}" \
    -p:InformationalVersion="${APP_VERSION}" \
    -p:IncludeSourceRevisionInInformationalVersion=false \
    -p:SourceRevisionId="${SOURCE_REVISION}" \
    -p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime

ARG APP_VERSION=0.0.0-local
ARG SOURCE_REVISION=unknown
ARG SOURCE_URL=https://github.com/int04/citus-manager

LABEL org.opencontainers.image.title="Citus Manager" \
      org.opencontainers.image.description="ASP.NET Core control plane for self-hosted Citus clusters" \
      org.opencontainers.image.source="${SOURCE_URL}" \
      org.opencontainers.image.revision="${SOURCE_REVISION}" \
      org.opencontainers.image.version="${APP_VERSION}"

RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl gnupg \
    && install --directory --mode=0755 /etc/apt/keyrings \
    && curl --fail --show-error --silent https://www.postgresql.org/media/keys/ACCC4CF8.asc \
       | gpg --dearmor --output /etc/apt/keyrings/postgresql.gpg \
    && echo "deb [signed-by=/etc/apt/keyrings/postgresql.gpg] https://apt.postgresql.org/pub/repos/apt noble-pgdg main" \
       > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install --yes --no-install-recommends \
       postgresql-client-14 \
       postgresql-client-15 \
       postgresql-client-16 \
       postgresql-client-17 \
       postgresql-client-18 \
    && rm --recursive --force /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./

RUN install --directory --owner="${APP_UID}" --group="${APP_UID}" \
      /var/lib/citus-manager/keys \
      /var/lib/citus-manager/backup-data \
      /var/lib/citus-manager/backup-spool

ENV ASPNETCORE_URLS=http://+:8080 \
    Security__DataProtectionKeyPath=/var/lib/citus-manager/keys \
    Backup__Storage__LocalRootPath=/var/lib/citus-manager/backup-data \
    Backup__Execution__SpoolPath=/var/lib/citus-manager/backup-spool

USER ${APP_UID}
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent --show-error --max-time 5 --output /dev/null http://127.0.0.1:8080/Account/Login || exit 1

ENTRYPOINT ["dotnet", "CitusManager.dll"]
