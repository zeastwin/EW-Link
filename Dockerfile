# Build stage
FROM mcr.microsoft.com/dotnet/nightly/sdk:10.0 AS build
WORKDIR /src

COPY ["EW-Link.csproj", "./"]
RUN dotnet restore ./EW-Link.csproj

COPY . .
RUN dotnet restore ./EW-Link.csproj
RUN dotnet publish ./EW-Link.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/nightly/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV RESOURCES_ROOT=/Data/resources

COPY --from=build /app/publish .
EXPOSE 8080

ENTRYPOINT ["dotnet", "EW-Link.dll"]
