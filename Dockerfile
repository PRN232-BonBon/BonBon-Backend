FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY BonBon.sln ./
COPY BonBon.API/BonBon.API.csproj BonBon.API/
COPY BonBon.Business/BonBon.Business.csproj BonBon.Business/
COPY BonBon.DataAccess/BonBon.DataAccess.csproj BonBon.DataAccess/
COPY BonBon.Entities/BonBon.Entities.csproj BonBon.Entities/
RUN dotnet restore BonBon.sln

COPY . .
RUN dotnet publish BonBon.API/BonBon.API.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "BonBon.API.dll"]
