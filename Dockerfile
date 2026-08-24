FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore Rag.sln
RUN dotnet publish src/Rag.Api/Rag.Api.csproj --configuration Release --no-restore --output /out/api /p:UseAppHost=false
RUN dotnet publish src/Rag.Operator/Rag.Operator.csproj --configuration Release --no-restore --output /out/operator /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
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
