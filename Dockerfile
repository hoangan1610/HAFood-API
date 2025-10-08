# ==============================
# Build stage
# ==============================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy everything
COPY . ./

# Restore dependencies
RUN dotnet restore "HAShop.Api.csproj"

# Build and publish
RUN dotnet publish "HAShop.Api.csproj" -c Release -o /app/publish

# ==============================
# Runtime stage
# ==============================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Copy published output
COPY --from=build /app/publish .

# Expose port 8080 (Render requirement)
EXPOSE 8080

# Start app
ENTRYPOINT ["dotnet", "HAShop.Api.dll"]
