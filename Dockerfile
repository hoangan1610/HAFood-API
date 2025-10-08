# ===== Build stage =====
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files and restore
COPY . .
RUN dotnet restore ./HAShop.Api/HAShop.Api.csproj

# Build and publish
RUN dotnet publish ./HAShop.Api/HAShop.Api.csproj -c Release -o /app/out

# ===== Runtime stage =====
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/out ./

# Expose port for Render
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "HAShop.Api.dll"]
