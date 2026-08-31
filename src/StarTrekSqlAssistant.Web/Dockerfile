FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY StarTrekSqlAssistant.Web.csproj .
RUN dotnet restore

COPY . .
# No --no-restore here. The restore above runs with only the .csproj present,
# and the SDK adds Microsoft.AspNetCore.App.Internal.Assets - the package that
# carries _framework/blazor.web.js - only when it can see .razor files. Publishing
# --no-restore against that first pass silently produces a static-web-assets
# manifest with no _framework entries, so blazor.web.js 404s at runtime and the
# page never becomes interactive. This second restore is cache-warm and cheap.
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "StarTrekSqlAssistant.Web.dll"]
