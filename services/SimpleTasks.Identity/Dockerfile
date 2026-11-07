FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY shared/ shared/
COPY services/SimpleTasks.Identity/ services/SimpleTasks.Identity/
RUN dotnet publish services/SimpleTasks.Identity/SimpleTasks.Identity.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SimpleTasks.Identity.dll"]
