# Builds one of the .NET images: api, migrator or maintenance. Build context is the repository root.
#   docker build -f apps/api/Dockerfile --build-arg PROJECT=TroViec.Api --target api .
#   docker build -f apps/api/Dockerfile --build-arg PROJECT=TroViec.Migrator --target migrator .
#   docker build -f apps/api/Dockerfile --build-arg PROJECT=TroViec.Maintenance --target maintenance .
# The SDK stage runs on the build machine and cross-publishes for TARGETARCH (amd64 / arm64).

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
ARG TARGETARCH
WORKDIR /src
COPY apps/api/ ./
# Aggregator configs embedded by TroViec.Api (path ../../../worker from src/TroViec.Api).
COPY apps/worker/models/aggregator/ /worker/models/aggregator/
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" -c Release -a "$TARGETARCH" -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "TroViec.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS migrator
WORKDIR /app
COPY --from=build /out ./
COPY db/migrations ./db/migrations
ENV MIGRATIONS_DIR=/app/db/migrations
USER $APP_UID
ENTRYPOINT ["dotnet", "TroViec.Migrator.dll"]
CMD ["migrate"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS maintenance
WORKDIR /app
COPY --from=build /out ./
USER $APP_UID
ENTRYPOINT ["dotnet", "TroViec.Maintenance.dll"]
