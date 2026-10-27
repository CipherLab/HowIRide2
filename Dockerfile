# One image recipe for every service: docker build --build-arg PROJECT=Detect .
# Multi-arch: builds natively on the Pi (arm64) or via `docker buildx build --platform linux/arm64`.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT=Ingest
WORKDIR /src
COPY src/Transit/Transit.csproj src/Transit/
COPY src/${PROJECT}/${PROJECT}.csproj src/${PROJECT}/
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj
COPY proto/ proto/
COPY src/Transit/ src/Transit/
COPY src/${PROJECT}/ src/${PROJECT}/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
ARG PROJECT=Ingest
ENV APP=${PROJECT}
WORKDIR /app
COPY --from=build /app .
# "$@" passes run-time arguments (docker compose run backtest --yesterday ...) through to the app.
ENTRYPOINT ["sh", "-c", "exec dotnet /app/$APP.dll \"$@\"", "--"]
