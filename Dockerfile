FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/FinanceFlow.Api/FinanceFlow.Api.csproj --configfile NuGet.Config
RUN dotnet publish src/FinanceFlow.Api/FinanceFlow.Api.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=https://+:8443
EXPOSE 8443
USER $APP_UID
ENTRYPOINT ["dotnet", "FinanceFlow.Api.dll"]
