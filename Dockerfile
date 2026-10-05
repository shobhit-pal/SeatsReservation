# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY src/SeatApi/SeatApi.csproj src/SeatApi/
RUN dotnet restore src/SeatApi/SeatApi.csproj

# Copy remaining source code and publish
COPY src/SeatApi/ src/SeatApi/
RUN dotnet publish src/SeatApi/SeatApi.csproj -c Release -o /out

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /out .

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_gcServer=0

EXPOSE 8080

USER $APP_UID

ENTRYPOINT ["dotnet", "SeatApi.dll"]
