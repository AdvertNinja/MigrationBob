
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src


COPY . .


RUN dotnet restore MigrationBob.Api/MigrationBob.Api.csproj
RUN dotnet publish MigrationBob.Api/MigrationBob.Api.csproj -c Release -o /app/publish


FROM mcr.microsoft.com/playwright/dotnet:v1.54.0-jammy 
WORKDIR /app


COPY --from=build /app/publish .


ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV PORT=8080
EXPOSE 8080

ENTRYPOINT ["dotnet","MigrationBob.Api.dll"]
