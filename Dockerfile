FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ThermalWatch.slnx Directory.Build.props Directory.Packages.props ./
COPY src/ThermalWatch.Core/ThermalWatch.Core.csproj src/ThermalWatch.Core/
COPY src/ThermalWatch.Telegram/ThermalWatch.Telegram.csproj src/ThermalWatch.Telegram/
COPY src/ThermalWatch.Api/ThermalWatch.Api.csproj src/ThermalWatch.Api/
RUN dotnet restore ThermalWatch.slnx

COPY src/ src/
RUN dotnet publish src/ThermalWatch.Api/ThermalWatch.Api.csproj \
    -c Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish/ ./

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "ThermalWatch.Api.dll"]
