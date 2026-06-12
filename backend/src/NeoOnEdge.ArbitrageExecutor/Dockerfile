FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY backend/src/SUS.EOS.Sharp/SUS.EOS.Sharp.csproj backend/src/SUS.EOS.Sharp/
COPY backend/src/NeoOnEdge.ArbitrageExecutor/NeoOnEdge.ArbitrageExecutor.csproj backend/src/NeoOnEdge.ArbitrageExecutor/
RUN dotnet restore backend/src/NeoOnEdge.ArbitrageExecutor/NeoOnEdge.ArbitrageExecutor.csproj

COPY backend/src/SUS.EOS.Sharp/ backend/src/SUS.EOS.Sharp/
COPY backend/src/NeoOnEdge.ArbitrageExecutor/ backend/src/NeoOnEdge.ArbitrageExecutor/
RUN dotnet publish backend/src/NeoOnEdge.ArbitrageExecutor/NeoOnEdge.ArbitrageExecutor.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8787
ENTRYPOINT ["dotnet", "NeoOnEdge.ArbitrageExecutor.dll"]
