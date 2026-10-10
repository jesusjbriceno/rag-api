# NOTE: the exact-publication-value argument is named PUBLICATION_VERSION rather
# than VERSION. MSBuild promotes environment variables to MSBuild properties, so
# a build argument named VERSION would overwrite the reserved MSBuild `Version`
# property during `dotnet restore` (a `v`-prefixed or `develop-<sha>` value is
# not a valid NuGet version string), failing restore with MSB4181 before any
# package is downloaded. PUBLICATION_VERSION avoids that collision.
ARG PUBLICATION_VERSION=0.1.0-rc.1
ARG REVISION=dev
ARG ASSEMBLY_VERSION=0.1.0-rc.1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Redeclared without defaults so the publication build args propagate into
# this stage. PUBLICATION_VERSION is the exact publication value (e.g.
# v0.1.0-rc.1 or develop-<sha>); ASSEMBLY_VERSION is the numeric SemVer for
# MSBuild Version (leading 'v' already stripped by the workflow for release
# tags).
ARG PUBLICATION_VERSION
ARG ASSEMBLY_VERSION

COPY . .
RUN dotnet restore Rag.sln
RUN dotnet publish src/Rag.Api/Rag.Api.csproj \
        --configuration Release --no-restore --output /out/api /p:UseAppHost=false \
        -p:Version="${ASSEMBLY_VERSION}" -p:InformationalVersion="${PUBLICATION_VERSION}"
RUN dotnet publish src/Rag.Operator/Rag.Operator.csproj \
        --configuration Release --no-restore --output /out/operator /p:UseAppHost=false \
        -p:Version="${ASSEMBLY_VERSION}" -p:InformationalVersion="${PUBLICATION_VERSION}"
RUN dotnet publish src/Rag.AdminApp.Host/Rag.AdminApp.Host.csproj \
        --configuration Release --no-restore --output /out/adminapp /p:UseAppHost=false \
        -p:Version="${ASSEMBLY_VERSION}" -p:InformationalVersion="${PUBLICATION_VERSION}"

# Admin SPA build stage. Node 22 LTS satisfies Vitest 5's engine range
# (^22.12.0 || ^24.0.0 || >=26.0.0); Node 23/25 are non-LTS and must not be used here
# or in CI. Only package manifests are copied first so `npm ci` layers cache; only
# source files are copied afterwards, so the repository's local node_modules never
# enters the image build.
FROM node:22-bookworm-slim AS admin-ui
WORKDIR /ui
COPY apps/admin-ui/package.json apps/admin-ui/package-lock.json ./
RUN npm ci
COPY apps/admin-ui/index.html apps/admin-ui/tsconfig.json apps/admin-ui/vite.config.ts ./
COPY apps/admin-ui/src ./src
RUN npm run build

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG PUBLICATION_VERSION
ARG REVISION
LABEL org.opencontainers.image.version=${PUBLICATION_VERSION} \
      org.opencontainers.image.revision=${REVISION}
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 rag \
    && useradd --uid 10001 --gid rag --create-home --shell /usr/sbin/nologin rag
WORKDIR /app
RUN mkdir content && chown rag:rag content

FROM runtime AS api
COPY --from=build --chown=rag:rag /out/api/ ./
USER rag
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "Rag.Api.dll"]

FROM runtime AS operator
COPY --from=build --chown=rag:rag /out/operator/ ./
USER rag
ENTRYPOINT ["dotnet", "Rag.Operator.dll"]

# Admin BFF + SPA host: only the published host output and the built static SPA assets
# enter the image (no Node, no source, no signing material). Non-root, private by
# default; Coolify exposes it only through its proxy/Cloudflare Access boundary.
FROM runtime AS admin
COPY --from=build --chown=rag:rag /out/adminapp/ ./
COPY --from=admin-ui --chown=rag:rag /ui/dist/ ./wwwroot
USER rag
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "Rag.AdminApp.Host.dll"]
