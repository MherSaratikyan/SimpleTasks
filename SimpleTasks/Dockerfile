FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY shared/ shared/
COPY SimpleTasks/ SimpleTasks/
RUN dotnet publish SimpleTasks/SimpleTasks.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# The sign-in key folder must belong to the app user, so a mounted volume can store the keys.
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys && chown -R $APP_UID:$APP_UID /home/app/.aspnet
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SimpleTasks.dll"]
