# Multi-arch: builds natively on the Pi (arm64) or via `docker buildx build --platform linux/arm64`.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Ingest/Ingest.csproj src/Ingest/
COPY src/Transit/Transit.csproj src/Transit/
RUN dotnet restore src/Ingest/Ingest.csproj
COPY proto/ proto/
COPY src/Transit/ src/Transit/
COPY src/Ingest/ src/Ingest/
RUN dotnet publish src/Ingest/Ingest.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENV Ingest__DataDir=/data
VOLUME /data
ENTRYPOINT ["dotnet", "Ingest.dll"]
