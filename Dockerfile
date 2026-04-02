FROM mcr.microsoft.com/dotnet/sdk:latest AS build
WORKDIR /src
COPY src/MailVerifier.Web/MailVerifier.Web.csproj src/MailVerifier.Web/
RUN dotnet restore src/MailVerifier.Web/MailVerifier.Web.csproj
COPY . .
WORKDIR /src/src/MailVerifier.Web
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.5-noble-chiseled-composite AS runtime
WORKDIR /app
VOLUME ["/data"]
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "MailVerifier.Web.dll"]
