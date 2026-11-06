FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.3 AS build
WORKDIR /src
COPY src/MailVerifier.Web/MailVerifier.Web.csproj src/MailVerifier.Web/
RUN dotnet restore src/MailVerifier.Web/MailVerifier.Web.csproj
COPY . .
WORKDIR /src/src/MailVerifier.Web
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.3-resolute-chiseled-composite AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
# The chiseled image has no curl/wget, so the app probes its own /healthz endpoint.
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD ["dotnet", "MailVerifier.Web.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "MailVerifier.Web.dll"]
