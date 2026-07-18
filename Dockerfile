FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7 AS final
WORKDIR /app
COPY --from=build /app/publish/ ./

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "ThermalWatch.Api.dll"]
