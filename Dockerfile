FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY InverterMonitor.csproj ./
COPY NuGet.Config ./
RUN dotnet restore --configfile ./NuGet.Config

COPY . ./
RUN dotnet publish InverterMonitor.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

COPY --from=build /app/publish ./

EXPOSE 8080
ENTRYPOINT ["dotnet", "InverterMonitor.dll"]
