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
