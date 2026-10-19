FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY global.json ShopCoursework.csproj ./
RUN dotnet restore ShopCoursework.csproj

COPY . .
RUN dotnet publish ShopCoursework.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish .
RUN mkdir -p /app/data /app/wwwroot/uploads /home/app/.aspnet/DataProtection-Keys \
    && chown -R $APP_UID:$APP_UID /app/data /app/wwwroot/uploads /home/app/.aspnet

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "ShopCoursework.dll"]
