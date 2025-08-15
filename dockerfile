
FROM mcr.microsoft.com/playwright/dotnet:v1.46.0-focal AS build
WORKDIR /src

COPY ./MigrationBob.sln .
COPY ./MigrationBob.Core/MigrationBob.Core.csproj ./MigrationBob.Core/
COPY ./MigrationBob.Api/MigrationBob.Api.csproj ./MigrationBob.Api/
RUN dotnet restore MigrationBob.sln

COPY . .
RUN dotnet publish MigrationBob.Api/MigrationBob.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/playwright/dotnet:v1.46.0-focal
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MigrationBob.Api.dll"]
