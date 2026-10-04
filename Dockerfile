FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Earth.ArcGIS.Gateway/Earth.ArcGIS.Gateway.csproj src/Earth.ArcGIS.Gateway/
RUN dotnet restore src/Earth.ArcGIS.Gateway/Earth.ArcGIS.Gateway.csproj

COPY src/Earth.ArcGIS.Gateway/ src/Earth.ArcGIS.Gateway/
RUN dotnet publish src/Earth.ArcGIS.Gateway/Earth.ArcGIS.Gateway.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

RUN useradd --create-home --uid 10001 gateway
COPY --from=build --chown=gateway:gateway /app/publish ./

USER gateway
EXPOSE 8080

ENTRYPOINT ["dotnet", "Earth.ArcGIS.Gateway.dll"]
