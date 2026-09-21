FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The build context is the REPOSITORY ROOT, not this folder. It has to be:
# the csproj names its packages without versions and without a TargetFramework,
# both of which come from Directory.Packages.props and Directory.Build.props at
# the root. MSBuild finds those by walking up from the project directory, so
# they must sit above it inside the image too - hence the src/... paths below.
#
# Copied before the sources, and separately from them, so that editing a .razor
# file does not invalidate the restore layer.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/StarTrekSqlAssistant.Web/StarTrekSqlAssistant.Web.csproj src/StarTrekSqlAssistant.Web/
RUN dotnet restore src/StarTrekSqlAssistant.Web/StarTrekSqlAssistant.Web.csproj

COPY src/StarTrekSqlAssistant.Web/ src/StarTrekSqlAssistant.Web/
# No --no-restore here. The restore above runs with only the .csproj present,
# and the SDK adds Microsoft.AspNetCore.App.Internal.Assets - the package that
# carries _framework/blazor.web.js - only when it can see .razor files. Publishing
# --no-restore against that first pass silently produces a static-web-assets
# manifest with no _framework entries, so blazor.web.js 404s at runtime and the
# page never becomes interactive. This second restore is cache-warm and cheap.
RUN dotnet publish src/StarTrekSqlAssistant.Web/StarTrekSqlAssistant.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Run as the non-root "app" account (uid 1654) the base image already ships;
# APP_UID is an ENV baked into mcr.microsoft.com/dotnet/aspnet. Port 8080 is
# above 1024, so an unprivileged process can still bind it - that is exactly
# why the .NET images default to 8080 rather than 80.
#
# Create the Data Protection key directory FIRST, while still root. The app's
# home is /home/app once USER switches, so the key ring moves there from
# /root/... and docker-compose.yml mounts its volume to match. Docker seeds a
# fresh named volume from the image directory's contents *and* its ownership -
# but only if the path exists in the image. Without this mkdir/chown, Docker
# creates the mountpoint root-owned, uid 1654 cannot write to it, and the app
# silently falls back to an in-memory key ring: it starts fine and the failure
# only shows up as "The antiforgery token could not be decrypted" in a browser
# tab holding a cookie from a previous container.
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys \
 && chown -R app:app /home/app/.aspnet
USER $APP_UID

ENTRYPOINT ["dotnet", "StarTrekSqlAssistant.Web.dll"]
