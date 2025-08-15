



FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ./MigrationBob.sln .
COPY ./MigrationBob.Core/MigrationBob.Core.csproj ./MigrationBob.Core/
COPY ./MigrationBob.Api/MigrationBob.Api.csproj ./MigrationBob.Api/

RUN dotnet restore ./MigrationBob.sln

COPY . .
RUN dotnet publish ./MigrationBob.Api/MigrationBob.Api.csproj -c Release -o /app/publish





FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
RUN dotnet tool install --global Microsoft.Playwright.CLI
ENV PATH="$PATH:/root/.dotnet/tools"
COPY --from=build /app/publish .
RUN playwright install --with-deps chromium

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MigrationBob.Api.dll"]
