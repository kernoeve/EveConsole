# EVE Console — background worker image
#
# The released Linux tarball, run with --headless: ESI polling, market pricing, contracts,
# zKillboard, alarms, the scheduler, retention and backups, with no window. Exactly one client
# among those sharing a database does this work; the rest read. See docker-compose.yml beside
# this file for the worker together with the PostgreSQL server it needs.
#
#   docker compose build
#   docker compose build --build-arg EVECONSOLE_VERSION=0.9.17
#
# ⚠️ Build it at the version your desktop clients run. The database records the version that last
# upgraded it, and a worker or a client behind that refuses to start — so a worker built from
# "latest" while the desktop clients stay a version back moves the database ahead of all of them.

# Ubuntu 24.04. Everything the self-contained tarball does not carry itself: ICU, OpenSSL, zlib.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0

# "latest", or a release number such as 0.9.17 (no leading v).
ARG EVECONSOLE_VERSION=latest

# pg_dump for the built-in PostgreSQL backups. ⚠️ At least the server's major version: the app
# refuses to back up with an older pg_dump, since that can produce a dump that will not restore.
# Ubuntu's own is 16, so this comes from the PostgreSQL project's repository, matched to the
# server in docker-compose.yml.
ARG PG_MAJOR=17

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl tzdata; \
    install -d /usr/share/postgresql-common/pgdg; \
    curl -fsSL -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
        https://www.postgresql.org/media/keys/ACCC4CF8.asc; \
    . /etc/os-release; \
    echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list; \
    apt-get update; \
    apt-get install -y --no-install-recommends postgresql-client-${PG_MAJOR}; \
    rm -rf /var/lib/apt/lists/*

RUN set -eux; \
    if [ "$EVECONSOLE_VERSION" = "latest" ]; then \
        url=https://github.com/kernoeve/EveConsole/releases/latest/download/EveConsole-linux-x64.tar.gz; \
    else \
        url=https://github.com/kernoeve/EveConsole/releases/download/v${EVECONSOLE_VERSION}/EveConsole-linux-x64.tar.gz; \
    fi; \
    mkdir -p /opt/eveconsole; \
    curl -fsSL "$url" | tar -xz -C /opt/eveconsole; \
    chmod +x /opt/eveconsole/EveConsole

# ⚠️ The data directory, named outright. The app keeps it under $XDG_DATA_HOME/EveConsole —
# config, the SDE cache, pictures and the pg_dump backups — and without this it would be inside
# the home directory of whichever user the container happens to run as.
ENV XDG_DATA_HOME=/data
RUN install -d -o app -g app /data
VOLUME /data

# The image's own unprivileged user. Nothing here needs root.
USER app
WORKDIR /opt/eveconsole

# ⚠️ No TTY and no display: --headless runs on Avalonia's headless platform, so neither is needed.
# It handles SIGTERM itself and releases its lease on the way out; docker stop sends exactly that.
STOPSIGNAL SIGTERM
ENTRYPOINT ["/opt/eveconsole/EveConsole", "--headless"]
