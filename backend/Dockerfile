FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY backend/src/NeoOnEdge.Core/NeoOnEdge.Core.csproj         backend/src/NeoOnEdge.Core/
COPY backend/src/NeoOnEdge.Indexer/NeoOnEdge.Indexer.csproj   backend/src/NeoOnEdge.Indexer/
COPY backend/src/NeoOnEdge.Workers/NeoOnEdge.Workers.csproj   backend/src/NeoOnEdge.Workers/
COPY backend/src/NeoOnEdge.Api/NeoOnEdge.Api.csproj           backend/src/NeoOnEdge.Api/

RUN dotnet restore backend/src/NeoOnEdge.Api/NeoOnEdge.Api.csproj

COPY backend/ backend/
RUN dotnet publish backend/src/NeoOnEdge.Api/NeoOnEdge.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
EXPOSE 5000
ENTRYPOINT ["dotnet", "NeoOnEdge.Api.dll"]
